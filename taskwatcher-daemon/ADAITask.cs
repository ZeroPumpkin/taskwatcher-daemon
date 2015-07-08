using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace taskwatcher_daemon
{
    class ADAITask
    {
        // This is the task ID on dev1
        public int TaskID;

        // These fields are pulled from dev1
        public string TaskName;
        public string TaskStatus;
        public string TaskRelease;

        // These fields are taskwatcher metadata
        public DateTime LastUpdated;

        // These fields are used by CouchDB
        public string _id;
        public string _rev;

        public ADAITask(int TaskID)
        {
            this.TaskID = TaskID;
            this._id = TaskID.ToString();
        }
    }
}
