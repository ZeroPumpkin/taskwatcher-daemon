using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace taskwatcher_daemon
{
    class Settings
    {
        public bool autoWatch = false;

        // These fields are used by CouchDB
        public const string _id = "settings";
        public string _rev;
    }
}
