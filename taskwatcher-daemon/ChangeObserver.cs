using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using Newtonsoft.Json;

namespace taskwatcher_daemon
{
    class ChangesFeedItem
    {
        public class Change
        {
            public string rev;
        }

        public int seq;
        public int last_seq = -1;
        public string id;
        public Change[] changes;
    }

    class ChangeObserver : IObserver<string>
    {
        Timer taskTimer = null;
        int period = 0;
        string ignoreUntilSeqNr = "1";

        public ChangeObserver(Timer taskTimer, int period)
        {
            this.taskTimer = taskTimer;
            this.period = period;
        }

        public void OnCompleted()
        {
            Debug.WriteLine("changesfeed complete");
        }

        public void OnError(Exception e)
        {
            throw (e);
        }

        public void OnNext(string changes)
        {
            Debug.WriteLine("Got change notification: " + changes);

            ChangesFeedItem change = JsonConvert.DeserializeObject<ChangesFeedItem>(changes);
            if (change != null && change.last_seq > Int32.Parse(ignoreUntilSeqNr))
            {
                Debug.WriteLine("Triggering update due to change on remote DB: " + changes);
                taskTimer.Change(0, period);
            }
            
            Debug.WriteLineIf(change != null && change.last_seq <= Int32.Parse(ignoreUntilSeqNr), "Changes will be ignored.");
        }

        public void SetIgnoreUntilSeqNr(string ignoreSeqNr)
        {
            this.ignoreUntilSeqNr = ignoreSeqNr;
        }
    }
}
