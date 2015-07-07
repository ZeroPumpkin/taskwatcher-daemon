using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MyCouch;

namespace taskwatcher_daemon
{
    class ProxyDbConnection : ProxyConnection, IDbConnection
    {
        public string DbName { get; private set; }

        public ProxyDbConnection(ConnectionInfo connectionInfo) : base(connectionInfo)
        {
            if (string.IsNullOrWhiteSpace(connectionInfo.DbName))
            {
                throw new FormatException(
                    string.Format(ExceptionStrings.CanNotExtractDbNameFromDbUri, Address.OriginalString));
            }

            DbName = connectionInfo.DbName;
        }
    }
}
