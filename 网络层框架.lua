
1 相关涉及类
{
	HjNetworkBase ==> 网络base类，封装一系列基础操作
	HjTcpNetwork ==> 继承HjNetworkBase，处理业务的数据包接收字节在这个类里

	IMessageQueue ==》 网络消息队列接口类
	MessageQueue ==》 继承IMessageQueue

	HjSemaphore ==》 信号量封装类，控制多线程


	StreamBufferPool ==> 自定义结构StreamBuffer的对象池和字节数组对象池，StreamBuffer内部也是用MemoryStream，网络消息中使用对象池，减少gc
}

2 C#层发送和接收消息处理： 发送和接收都各自开了一个线程，其实完全可以用异步方法对应的线程池来弄
{
	###########发送 ==> 单独开了一个线程处理，HjTcpNetwork的SendThread方法里
	{
		HjTcpNetwork.SendMessage : 业务层调用方法发送数据，只是单纯的把数据放入队列里，在子线程里处理队列
		{
			//自定义数据包封装结构，获取包数据需要知道数据包长度避免粘包或丢包
            ByteBuffer buffer = new ByteBuffer();
            buffer.WriteInt(msgObj.Length); //数据包长度
            buffer.WriteBytes(msgObj); //数据包
            
            //把字节数组放到发送队列
            mSendMsgQueue.Add(buffer.ToBytes());
            mSendSemaphore.ProduceResrouce();
		}

		子线程开了一个while循环，处理发送消息队列mSendMsgQueue，调用socket.Send方法进行发送数据
		{
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
		}
	}
 
	###########接收 ==》 单独开了一个线程处理， HjNetworkBase.ReceiveThread方法里
	{
		循环接收数据包长度以及数据包内容，子类HjTcpNetwork.DoReceive方法里处理接收到的字节数组, 数据包字节数组，放到接收消息队列里
		{
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

	            //数据包字节数组，放到处理消息队列里
	            mReceiveMsgQueue.Add(bytes);

	            // 下一次组包
	            start += msgLen;
	        }
		}

		处理接收消息队列，HjNetworkBase.UpdatePacket方法里，回调到业务层
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
	
}

3 业务层发送和处理消息 HallConnector.lua文件里
{

	########## 业务层发送消息，self.hallSocket就是一个HjTcpNetwork实例
	{
		local function SendMessage(self, msg_id, msg)
			local bytes = ""
			
			-- 消息ID，设置为大端编码，无符号整数
			--https://blog.csdn.net/beyond706/article/details/105949783
			bytes = bytes..string.pack("=I2",msg_id);
			if(msg)then
				local msg_bytes=pb.encode(MsgIDMap[msg_id], msg)
				bytes = bytes..msg_bytes -- 字符数组拼接？
			end
			
			-- Logger.Debug("send message: \ncmdId："..msg_id.."\nbyte count："..#bytes.."\ntable->"..(msg and table.dump(msg) or "{}"));
			self.hallSocket:SendMessage(bytes);
		end
	}

	########## 业务层接收消息，self.hallSocket就是一个HjTcpNetwork实例
	{
		local function OnReceivePackage(self, receive_bytes)
			local msg_id = string.unpack("=I2",receive_bytes)
			local msg_bytes = string.sub(receive_bytes, 3) --从第3个字节开始就是消息体内容

			if(self.handlers[msg_id] == nil)then
				Logger.Error("msg_id 未绑定函数"..msg_id);
				return;
			end

			--Logger.Debug("receive message cmdId:"..msg_id.." | msg_bytes.len:"..#msg_bytes);

			local msg = nil;
			if(msg_bytes ~= nil)then
				msg = pb.decode(MsgIDMap[msg_id], msg_bytes)
			end

			self.handlers[msg_id](msg_id, msg) -- 回调给业务层消息处理函数
		end
	}
	
}