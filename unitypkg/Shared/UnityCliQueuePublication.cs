using System;

namespace UnityCli
{
    /// <summary>队列map与队列节点的原子publication骨架；任一阶段失败都回滚已发布部分。</summary>
    public static class UnityCliQueuePublication
    {
        public static void Publish(Action addMap, Action addQueue, Action removeMap, Action removeQueue, Action markPublished)
        {
            if (addMap == null || addQueue == null || removeMap == null || removeQueue == null || markPublished == null)
                throw new ArgumentNullException("publication action");
            var mapAdded = false;
            var queueAdded = false;
            try
            {
                addMap();
                mapAdded = true;
                addQueue();
                queueAdded = true;
                markPublished();
            }
            catch
            {
                if (queueAdded) { try { removeQueue(); } catch { } }
                if (mapAdded) { try { removeMap(); } catch { } }
                throw;
            }
        }
    }
}
