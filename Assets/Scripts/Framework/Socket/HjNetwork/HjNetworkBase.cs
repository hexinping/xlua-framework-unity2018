﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using CustomDataStruct;
using System.Threading;

namespace Networks
{
    public enum SOCKSTAT
    {
        CLOSED = 0,
        CONNECTING,
        CONNECTED,
    }

    public abstract class HjNetworkBase
    {
        public Action<object, int, string> OnConnect = null;
        public Action<object, int, string> OnClosed = null;
        public Action<byte[]> ReceivePkgHandle = null;

        private List<HjNetworkEvt> mNetworkEvtList = null;
        private object mNetworkEvtLock = null;
        
        //最大发送字节数
        protected int mMaxBytesOnceSent = 0;
        
        //最大接收字节数，UpdateNetwork 主线程驱动
        protected int mMaxReceiveBuffer = 0;

        protected Socket mClientSocket = null;
        protected string mIp;
        protected int mPort;
        protected volatile SOCKSTAT mStatus = SOCKSTAT.CLOSED;

        //接收线程
        private Thread mReceiveThread = null;
        
        //https://www.cnblogs.com/gjhjoy/p/3556709.html
        //一个变量经 volatile修饰后在所有线程中必须是同步的；任何线程中改变了它的值，所有其他线程立即获取到了相同的值
        private volatile bool mReceiveWork = false; 
        private List<byte[]> mTempMsgList = null;
        
        //处理的消息队列
        protected IMessageQueue mReceiveMsgQueue = null;
        
    
        public HjNetworkBase(int maxBytesOnceSent = 1024 * 512, int maxReceiveBuffer = 1024 * 1024 * 2)
        {
            mStatus = SOCKSTAT.CLOSED;
            
            mMaxBytesOnceSent = maxBytesOnceSent;
            mMaxReceiveBuffer = maxReceiveBuffer;

            mNetworkEvtList = new List<HjNetworkEvt>();
            mNetworkEvtLock = new object();
            mTempMsgList = new List<byte[]>();
            mReceiveMsgQueue = new MessageQueue();
        }

        public virtual void Dispose()
        {
            Close();
        }

        public Socket ClientSocket
        {
            get
            {
                return mClientSocket;
            }
        }

        public void SetHostPort(string ip, int port)
        {
            mIp = ip;
            mPort = port;
        }
        
        protected abstract void DoConnect();
        public void Connect()
        {
            //连接之前先清理一遍
            Close();

            int result = ESocketError.NORMAL;
            string msg = null;
            try
            {
                DoConnect();
            }
            catch (ObjectDisposedException ex)
            {
                result = ESocketError.ERROR_3;
                msg = ex.Message;
                mStatus = SOCKSTAT.CLOSED;
            }
            catch (Exception ex)
            {
                result = ESocketError.ERROR_4;
                msg = ex.Message;
                mStatus = SOCKSTAT.CLOSED;
            }
            finally
            {
                if (result != ESocketError.NORMAL && OnConnect != null)
                {
                    ReportSocketConnected(result, msg);
                }
            }
        }

        protected virtual void OnConnected()
        {
            StartAllThread();
            mStatus = SOCKSTAT.CONNECTED;
            ReportSocketConnected(ESocketError.NORMAL, "Connect successfully");
        }

        public virtual void StartAllThread()
        {
            if (mReceiveThread == null)
            {
                //开启接收线程
                mReceiveThread = new Thread(ReceiveThread);
                mReceiveWork = true; //所有线程同步这个变量
                mReceiveThread.Start(null);
            }
        }

        public virtual void StopAllThread()
        {
            mReceiveMsgQueue.Dispose();

            if (mReceiveThread != null)
            {
                mReceiveWork = false;
                mReceiveThread.Join();
                mReceiveThread = null;
            }
        }

        protected virtual void DoClose()
        {
            //只单纯的close吗，进行了收发数据需要shutDown吧？
            mClientSocket.Close();
            if (mClientSocket.Connected)
            {
                throw new InvalidOperationException("Should close socket first!");
            }
            mClientSocket = null;
            StopAllThread();
        }

        public virtual void Close()
        {
            if (mClientSocket == null) return;

            mStatus = SOCKSTAT.CLOSED;
            try
            {
                DoClose();
                ReportSocketClosed(ESocketError.ERROR_5, "Disconnected!");
            }
            catch (Exception e)
            {
                ReportSocketClosed(ESocketError.ERROR_4, e.Message);
            }
        }

        protected void ReportSocketConnected(int result, string msg)
        {
            if (OnConnect != null)
            {
                AddNetworkEvt(new HjNetworkEvt(this, result, msg, OnConnect));
            }
        }

        protected void ReportSocketClosed(int result, string msg)
        {
            if (OnClosed != null)
            {
                AddNetworkEvt(new HjNetworkEvt(this, result, msg, OnClosed));
            }
        }

        protected abstract void DoReceive(StreamBuffer receiveStreamBuffer, ref int bufferCurLen);
        private void ReceiveThread(object o)
        {
            StreamBuffer receiveStreamBuffer = StreamBufferPool.GetStream(mMaxReceiveBuffer, false, true);
            int bufferCurLen = 0;
            while (mReceiveWork)
            {
                try
                {
                    if (!mReceiveWork) break;
                    if (mClientSocket != null)
                    {
                        //bufferCurLen是用来记录本次接收了多少字节的，
                        //mClientSocket.Receive第三个参数代表最大接收多少个字节，有时会接收不满，循环接收数据包，直到接收完全
                        int bufferLeftLen = receiveStreamBuffer.size - bufferCurLen;
                        //开启接收数据，
                        int readLen = mClientSocket.Receive(receiveStreamBuffer.GetBuffer(), bufferCurLen, bufferLeftLen, SocketFlags.None);
                        if (readLen == 0) throw new ObjectDisposedException("DisposeEX", "receive from server 0 bytes,closed it");
                        if (readLen < 0) throw new Exception("Unknow exception, readLen < 0" + readLen);

                        bufferCurLen += readLen;
                        DoReceive(receiveStreamBuffer, ref bufferCurLen);
                        if (bufferCurLen == receiveStreamBuffer.size)
                            throw new Exception("Receive from sever no enough buff size:" + bufferCurLen);
                    }
                }
                catch (ObjectDisposedException e)
                {
                    ReportSocketClosed(ESocketError.ERROR_3, e.Message);
                    break;
                }
                catch (Exception e)
                {
                    ReportSocketClosed(ESocketError.ERROR_4, e.Message);
                    break;
                }
            }

            StreamBufferPool.RecycleStream(receiveStreamBuffer);
            if (mStatus == SOCKSTAT.CONNECTED)
            {
                mStatus = SOCKSTAT.CLOSED;
            }
        }
        
        protected void AddNetworkEvt(HjNetworkEvt evt)
        {
            lock (mNetworkEvtLock)
            {
                mNetworkEvtList.Add(evt);
            }
        }

        private void UpdateEvt()
        {
            lock (mNetworkEvtLock)
            {
                try
                {
                    for (int i = 0; i < mNetworkEvtList.Count; ++i)
                    {
                        HjNetworkEvt evt = mNetworkEvtList[i];
                        evt.evtHandle(evt.sender, evt.result, evt.msg);
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("Got the fucking exception :" + e.Message);
                }
                finally
                {
                    mNetworkEvtList.Clear();
                }
            }
        }

        private void UpdatePacket()
        {
            if (!mReceiveMsgQueue.Empty())
            {
                mReceiveMsgQueue.MoveTo(mTempMsgList);

                try
                {
                    for (int i = 0; i < mTempMsgList.Count; ++i)
                    {
                        var objMsg = mTempMsgList[i];
                        if (ReceivePkgHandle != null)
                        {
                            //回调到业务层，参数是字节数组，
                            ReceivePkgHandle(objMsg);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("Got the fucking exception :" + e.Message);
                }
                finally
                {
                    for (int i = 0; i < mTempMsgList.Count; ++i)
                    {
                        //回收buffer字节数组
                        StreamBufferPool.RecycleBuffer(mTempMsgList[i]);
                    }
                    mTempMsgList.Clear();
                }
            }
        }

        public virtual void UpdateNetwork()
        {
            UpdatePacket();
            UpdateEvt();
        }

        // 发送消息的时候要注意对buffer进行拷贝，网络层发送完毕以后会对buffer执行回收
        public virtual void SendMessage(byte[] msgObj)
        {
        }

        public bool IsConnect()
        {
            return mStatus == SOCKSTAT.CONNECTED;
        }
    }
}
