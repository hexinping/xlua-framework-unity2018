//#define LOG_SEND_BYTES
//#define LOG_RECEIVE_BYTES
using System;
using System.Net.Sockets;
using CustomDataStruct;
using System.Threading;
using System.Collections.Generic;
using XLua;

namespace Networks
{
    [Hotfix]
    public class HjTcpNetwork : HjNetworkBase
    {
        private Thread mSendThread = null;
        //一个变量经 volatile修饰后在所有线程中必须是同步的；任何线程中改变了它的值，所有其他线程立即获取到了相同的值
        private volatile bool mSendWork = false;
        
        //信号量封装
        private HjSemaphore mSendSemaphore = null;
        
        //发送消息队列
        protected IMessageQueue mSendMsgQueue = null;

        public HjTcpNetwork(int maxBytesOnceSent = 1024 * 100, int maxReceiveBuffer = 1024 * 512) : base(maxBytesOnceSent, maxReceiveBuffer)
        {
            mSendSemaphore = new HjSemaphore();
            mSendMsgQueue = new MessageQueue();
        }

        public override void Dispose()
        {
            mSendMsgQueue.Dispose();
            base.Dispose();
        }

        protected override void DoConnect()
        {

            AddressFamily newAddressFamily = AddressFamily.InterNetwork;
            IPv6SupportMidleware.getIPType(mIp, mPort.ToString(), out newAddressFamily);
            //创建一个客户端的socket，tcp连接
            mClientSocket = new Socket(newAddressFamily, SocketType.Stream, ProtocolType.Tcp);
            //异步连接服务器
            mClientSocket.BeginConnect(mIp, mPort, (IAsyncResult ia) =>
            {
                //服务器连接成功的回调
                mClientSocket.EndConnect(ia); //结束连接
                OnConnected();
            }, null);
            mStatus = SOCKSTAT.CONNECTING;
        }
        
        protected override void DoClose()
        {
            // 关闭socket，Tcp下要等待现有数据发送、接受完成
            // https://msdn.microsoft.com/zh-cn/library/system.net.sockets.socket.shutdown(v=vs.90).aspx
            // mClientSocket.Shutdown(SocketShutdown.Both);
            base.DoClose();
        }

        public override void StartAllThread()
        {
            //开启接收数据线程
            base.StartAllThread();

            if (mSendThread == null)
            {
                //开启发送数据线程，while(true)循环处理发送消息队列
                mSendThread = new Thread(SendThread);
                mSendWork = true; //线程同步变量
                mSendThread.Start(null);
            }
        }

        public override void StopAllThread()
        {
            base.StopAllThread();
            //先把队列清掉
            mSendMsgQueue.Dispose();

            if (mSendThread != null)
            {
                mSendWork = false;
                mSendSemaphore.ProduceResrouce();// 唤醒线程
                mSendThread.Join();// 等待子线程退出
                mSendThread = null;
            }
        }

        private void SendThread(object o)
        {
            List<byte[]> workList = new List<byte[]>(10);

            while (mSendWork)
            {
                if (!mSendWork)
                {
                    break;
                }

                if (mClientSocket == null || !mClientSocket.Connected)
                {
                    continue;
                }
                
                //信号量减一
                mSendSemaphore.WaitResource();
                if (mSendMsgQueue.Empty())
                {
                    continue;
                }
                //把发送消息队列的出局放到workList里，最多10个
                mSendMsgQueue.MoveTo(workList);
                try
                {
                    for (int k = 0; k < workList.Count; ++k)
                    {
                        var msgObj = workList[k];
                        if (mSendWork)
                        {
                            //调用socket的发送数据方式 ==》发送到已连接的指定的数据的字节数
                            mClientSocket.Send(msgObj, msgObj.Length, SocketFlags.None);
                        }
                    }
                }
                catch (ObjectDisposedException e)
                {
                    ReportSocketClosed(ESocketError.ERROR_1, e.Message);
                    break;
                }
                catch (Exception e)
                {
                    ReportSocketClosed(ESocketError.ERROR_2, e.Message);
                    break;
                }
                finally
                {
                    for (int k = 0; k < workList.Count; ++k)
                    {
                        var msgObj = workList[k];
                        //把自己数组放进池子里，下一次重复利用
                        StreamBufferPool.RecycleBuffer(msgObj);
                    }
                    workList.Clear();
                }
            }
            
            if (mStatus == SOCKSTAT.CONNECTED)
            {
                mStatus = SOCKSTAT.CLOSED;
            }
        }

        protected override void DoReceive(StreamBuffer streamBuffer, ref int bufferCurLen)
        {
            try
            {
                // 组包、拆包
                byte[] data = streamBuffer.GetBuffer();
                int start = 0;
                streamBuffer.ResetStream();
                while (true)
                {
                    if (bufferCurLen - start < sizeof(int))
                    {
                        //连数据头都没接收完或者完全接收完，直接返回
                        break;
                    }
                    //获取前4个字节，表示数据包长度
                    int msgLen = BitConverter.ToInt32(data, start);
                    if (bufferCurLen < msgLen + sizeof(int))
                    {
                        //数据包未接收完，直接返回
                        break;
                    }

                    // 提取字节流，去掉开头表示长度的4字节
                    start += sizeof(int);
                    var bytes = streamBuffer.ToArray(start, msgLen);
#if LOG_RECEIVE_BYTES
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        sb.AppendFormat("{0}\t", bytes[i]);
                    }
                    Logger.Log("HjTcpNetwork receive bytes : " + sb.ToString());
#endif
                    //数据包字节数组，放到处理消息队列里
                    mReceiveMsgQueue.Add(bytes);

                    // 下一次组包
                    start += msgLen;
                }

                if (start > 0)
                {
                    //完全接收完
                    
                    bufferCurLen -= start;
                    //把data数据拷贝到streamBuffer的mBuffer字段里
                    streamBuffer.CopyFrom(data, start, 0, bufferCurLen);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(string.Format("Tcp receive package err : {0}\n {1}", ex.Message, ex.StackTrace));
            }
        }

        public override void SendMessage(byte[] msgObj)
        {
#if LOG_SEND_BYTES
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < msgObj.Length; i++)
            {
                sb.AppendFormat("{0}\t", msgObj[i]);
            }
            Logger.Log("HjTcpNetwork send bytes : " + sb.ToString());
#endif
            //自定义数据包封装结构，获取包数据需要知道数据包长度避免粘包或丢包
            ByteBuffer buffer = new ByteBuffer();
            buffer.WriteInt(msgObj.Length); //数据包长度
            buffer.WriteBytes(msgObj); //数据包
            
            //把字节数组放到发送队列
            mSendMsgQueue.Add(buffer.ToBytes());
            mSendSemaphore.ProduceResrouce();
        }
    }

#if UNITY_EDITOR
    public static class HjTcpNetworkExporter
    {
        [LuaCallCSharp]
        public static List<Type> LuaCallCSharp = new List<Type>()
        {
            typeof(HjTcpNetwork),
            typeof(ByteBuffer),
        };

        [CSharpCallLua]
        public static List<Type> CSharpCallLua = new List<Type>()
        {
            typeof(Action<object, int, string>),
            typeof(Action<byte[]>),
        };
    }
#endif
}