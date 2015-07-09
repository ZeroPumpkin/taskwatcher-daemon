using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using MyCouch;
using MyCouch.Requests;
using MyCouch.Responses;
using Newtonsoft.Json;
using System.Threading;
using System.Diagnostics;

namespace taskwatcher_daemon
{
    class Program
    {
        const int TIMER_PERIOD = 30 * 1000 * 60;
        const string DB = "dev1.avaloq";

        static Timer workTimer = null;
        static string connString = null;
        static string username = null;
        static string password = null;
        static ChangeObserver changeObserver = null;

        static void Main(string[] args)
        {
            MainAsync(args);
        }

        static void MainAsync(string[] args)
        {
            // Display banner
            Console.WriteLine("╔═══════════════════════════════════════╗");
            Console.WriteLine("║         taskwatcher for ADAI          ║");
            Console.WriteLine("╚═══════════════════════════════════════╝");
            Console.WriteLine();

            // Collect credentials
            Console.Write("Enter username for " + DB + ": ");
            username = Console.ReadLine();
            Console.Write("Enter password (hidden): ");
            password = GetPassword();

            // Compose connection string
            connString = "Data Source=" + DB + "; User Id=" + username + "; Password=" + password;

            ConnectionInfo connInfo = new ConnectionInfo(new Uri("http://www.zeropumpkin.iriscouch.com/taskwatcher-fmb"))
            {
                // Connection never times out, since we use a continuous change feed
                Timeout = TimeSpan.FromMilliseconds(System.Threading.Timeout.Infinite)
            };

            MyCouchClientBootstrapper bootstrapper = new MyCouchClientBootstrapper();
            bootstrapper.DbConnectionFn = new Func<ConnectionInfo, IDbConnection>(ProxyDbConnection);

            using (MyCouchClient couch = new MyCouchClient(connInfo))
            {
                Console.Write("Checking remote DB...");

                // Create the database if it does not exist
                couch.Database.PutAsync().Wait();

                Console.WriteLine("exists");

                //using (MyCouchStore store = new MyCouchStore(couch))
                //{
                //    while (true)
                //    {
                //        Console.Write("Enter task ID: ");
                //        string taskID = Console.ReadLine();

                //        //ADAITask t = await store.GetByIdAsync<ADAITask>(taskID);

                //        ADAITask t = new ADAITask(Int32.Parse(taskID));
                //        t.LastUpdated = DateTime.Now;

                //        Console.WriteLine("Storing doc...");
                //        Task<ADAITask> task = store.StoreAsync(t);
                //        task.Wait();
                //        Console.WriteLine("Doc stored: " + t._id);
                //    }
                //}

                // First get the changes feed to get the last seq nr
                GetChangesRequest getChangesRequest = new GetChangesRequest
                {
                    Feed = ChangesFeed.Normal
                };

                Task<ChangesResponse> changesTask = couch.Changes.GetAsync(getChangesRequest);
                changesTask.Wait();
                string lastSeqNr = changesTask.Result.LastSeq;

                MyCouchStore store = new MyCouchStore(couch);

                // Start timer - callback is called immediately
                workTimer = new Timer(WorkTimerCallback, store, 0, TIMER_PERIOD);
                
                // Now start continuous observation using the last seq nr
                getChangesRequest = new GetChangesRequest
                {
                    Feed = ChangesFeed.Continuous,
                    Since = lastSeqNr
                };
                CancellationToken cancellationToken = new CancellationToken();
                IObservable<string> changes = couch.Changes.ObserveContinuous(getChangesRequest, cancellationToken);

                changeObserver = new ChangeObserver(workTimer, TIMER_PERIOD);
                changes.Subscribe(changeObserver);
                Debug.WriteLine("Started continuous observation, from seq nr " + lastSeqNr);

                while (true)
                {
                    // Do nothing
                }
            }
        }

        static string GetPassword()
        { 
            ConsoleKeyInfo keyInfo = new ConsoleKeyInfo();
            string password = "";

            while (true) {
                keyInfo = Console.ReadKey(true);

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return password;
                }
                else if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    Console.Write("\b");
                }
                else
                {
                    password = password + keyInfo.KeyChar;
                }
            }
        }

        static void WorkTimerCallback(object state)
        {
            MyCouchStore store = (MyCouchStore) state;

            Console.Write("Polling tasklist...");

            // Get all docs
            Query q = new Query("_all_docs");
            q.IncludeDocs = true;
            Task<IEnumerable<Row<string, ADAITask>>> queryTask = store.QueryAsync<string, ADAITask>(q);
            queryTask.Wait();
            IEnumerable<Row<string, ADAITask>> rows = queryTask.Result;

            Console.WriteLine("done - " + queryTask.Result.Count() + " tasks fetched");

            if (queryTask.Result.Count() == 0)
            {
                return;
            }

            Console.Write("Connecting to " + DB + "...");
            OracleConnection conn = null;

            try
            {
                conn = new OracleConnection(connString);
                conn.Open();

                // Open session
                OracleCommand cmdOpenSession = new OracleCommand("e.env_session_intf#.open_session", conn);
                cmdOpenSession.CommandType = System.Data.CommandType.StoredProcedure;
                cmdOpenSession.ExecuteNonQuery();

                Console.WriteLine("done");

                string inClause = "";
                foreach (Row<string, ADAITask> row in rows)
                {
                    inClause = inClause + row.IncludedDoc.TaskID + ",";
                }
                inClause = inClause.TrimEnd(',');

                OracleCommand cmd = new OracleCommand();
                cmd.Connection = conn;
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.CommandText = "select env_task.id, env_task.name, env_wfc_status.name, env_release.user_id " +
                                  "from e.env_task, e.env_wfc_status, e.env_release " +
                                  "where env_task.id in (" + inClause + ") " +
                                  "  and env_task.env_wfc_status_id = env_wfc_status.id " +
                                  "  and env_task.env_release_id = env_release.id";
                Console.Write("Running query...");

                Debug.WriteLine(cmd.CommandText);
                OracleDataReader reader = cmd.ExecuteReader();

                Console.WriteLine("done");

                while (reader.Read())
                {
                    Row<string, ADAITask> t = rows.Where(e => e.IncludedDoc.TaskID == reader.GetInt32(0)).Single();
                    t.IncludedDoc.TaskName = reader.GetString(1);
                    t.IncludedDoc.TaskStatus = reader.GetString(2);
                    t.IncludedDoc.TaskRelease = reader.GetString(3);
                    t.IncludedDoc.LastUpdated = DateTime.Now;

                    Debug.WriteLine("Task ID: " + t.IncludedDoc.TaskID + ", Task Name: " + t.IncludedDoc.TaskName + ", Task Status: " + t.IncludedDoc.TaskStatus +
                        ", Task Release: " + t.IncludedDoc.TaskRelease);
                }

                reader.Close();
            }
            catch (OracleException e)
            {
                Console.Error.WriteLine(e.Message);
                return;
            }
            finally
            {
                if (conn != null)
                {
                    conn.Close();
                }
            }

            foreach (Row<string, ADAITask> row in rows)
            {
                Console.Write("Updating doc for task " + row.IncludedDoc.TaskID + "...");
                store.StoreAsync<ADAITask>(row.IncludedDoc).Wait();
                Console.WriteLine("done");
            }

            GetChangesRequest getChangesRequest = new GetChangesRequest
            {
                Feed = ChangesFeed.Normal
            };

            Task<ChangesResponse> changesTask = store.Client.Changes.GetAsync(getChangesRequest);
            changesTask.Wait();
            changeObserver.SetIgnoreUntilSeqNr(changesTask.Result.LastSeq);
        }

        static IDbConnection ProxyDbConnection(ConnectionInfo connInfo)
        {
            return new ProxyDbConnection(connInfo);
        }
    }
}
