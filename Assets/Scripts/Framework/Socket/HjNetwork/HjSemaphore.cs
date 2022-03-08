using System.Threading;

namespace Networks
{
    class HjSemaphore
    {
        int mResource;
        object mResourceObj = null;

        public HjSemaphore()
        {
            mResource = 0;
            mResourceObj = new object();
        }
        
        public void WaitResource()
        {
            WaitResource(1);
        }

        public void WaitResource(int count)
        {
            while (true)
            {
                lock (mResourceObj)
                {
                    if (mResource >= count)
                    {
                        mResource -= count;
                        return;
                    }
                }
                lock (this)
                {
                    //释放锁、进入等待队列 ==>会阻塞线程，阻塞的同时但会释放锁，再次获得锁的时候，需要monitor.Pulse唤醒
                    Monitor.Wait(this);
                }
            }
        }

        public void ProduceResrouce()
        {
            ProduceResrouce(1);
        }
        
        public void ProduceResrouce(int count)
        {
            lock (mResourceObj)
            {
                mResource += count;
            }
            
            lock (this)
            {
                //释放锁，并唤醒等待队列中的线程使其进入就绪队列
                Monitor.Pulse(this);
            }
        }
    }
}
