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
        const int TIMER_PERIOD = 60 * 1000 * 60;
        const string DB = "dev1.avaloq";

        static Timer workTimer = null;
        static string connString = null;
        static string username = null;
        static string password = null;

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

            //MyCouchClientBootstrapper bootstrapper = new MyCouchClientBootstrapper();
            //bootstrapper.DbConnectionFn = new Func<ConnectionInfo, IDbConnection>(ProxyDbConnection);

            using (MyCouchClient couch = new MyCouchClient(connInfo))
            {
                Console.Write("Checking remote DB...");

                // Create the database if it does not exist
                couch.Database.PutAsync().Wait();

                Task<DocumentHeaderResponse> headTask = couch.Documents.HeadAsync("_design/tasks");
                headTask.Wait();

                // Create design document with tasks view
                if (!headTask.Result.IsSuccess)
                {
                    string taskDesignDoc = "{ \"language\": \"javascript\", \"views\": { \"tasks\": { \"map\": \"function(doc) { if (doc.$doctype != 'adaiTask') return; emit(doc._id, { 'rev': doc._rev }); }\" } } }";
                    couch.Documents.PutAsync("_design/tasks", taskDesignDoc).Wait();
                }

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

                //// First get the changes feed to get the last seq nr
                //GetChangesRequest getChangesRequest = new GetChangesRequest
                //{
                //    Feed = ChangesFeed.Normal
                //};

                //Task<ChangesResponse> changesTask = couch.Changes.GetAsync(getChangesRequest);
                //changesTask.Wait();
                //string lastSeqNr = changesTask.Result.LastSeq;

                // Start timer - callback is called immediately
                workTimer = new Timer(WorkTimerCallback, couch, 0, TIMER_PERIOD);
                
                //// Now start continuous observation using the last seq nr
                //getChangesRequest = new GetChangesRequest
                //{
                //    Feed = ChangesFeed.Continuous,
                //    Since = lastSeqNr
                //};
                //CancellationToken cancellationToken = new CancellationToken();
                //IObservable<string> changes = couch.Changes.ObserveContinuous(getChangesRequest, cancellationToken);

                //changeObserver = new ChangeObserver(workTimer, TIMER_PERIOD);
                //changes.Subscribe(changeObserver);
                //Debug.WriteLine("Started continuous observation, from seq nr " + lastSeqNr);

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
            MyCouchClient couch = (MyCouchClient) state;
            List<ADAITask> taskList = new List<ADAITask>();

            Console.Write("Getting settings...");
            Task<GetEntityResponse<Settings>> settingsTask = couch.Entities.GetAsync<Settings>("settings");
            settingsTask.Wait();

            Settings settings;

            if (settingsTask.Result.IsSuccess)
            {
                settings = settingsTask.Result.Content;
                Console.WriteLine("done");
            } 
            else
            {
                settings = new Settings();
                Console.WriteLine("error - using defaults");
            }
            Console.WriteLine("Current settings: autoWatch = " + settings.autoWatch);

            Console.Write("Polling tasklist...");

            QueryViewRequest req = new QueryViewRequest("tasks", "tasks").Configure(q => q.IncludeDocs(true));
            Task<ViewQueryResponse<string, ADAITask>> queryTask = couch.Views.QueryAsync<string, ADAITask>(req);
            queryTask.Wait();

            Console.WriteLine("done - " + queryTask.Result.TotalRows + " tasks fetched");

            if (queryTask.Result.TotalRows == 0 && !settings.autoWatch)
            {
                Console.WriteLine("Finished! Waiting for next polling interval...");
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

                OracleCommand cmd = new OracleCommand();
                cmd.Connection = conn;
                cmd.CommandType = System.Data.CommandType.Text;

                // Copy task IDs to a list that we then use in an OracleParameter for the IN clause
                foreach (ViewQueryResponse<string, ADAITask>.Row row in queryTask.Result.Rows)
                {
                    //taskIdList.Add(row.IncludedDoc.TaskID);
                    OracleParameter par = new OracleParameter(":t" + cmd.Parameters.Count, OracleDbType.Int32);
                    par.Value = row.IncludedDoc.TaskID;

                    cmd.Parameters.Add(par);
                }

                StringBuilder inClause = new StringBuilder();
                foreach (OracleParameter par in cmd.Parameters)
                {
                    inClause.AppendFormat("{0},", par.ParameterName);
                }

                if (inClause.Length > 0)
                {
                    inClause.Remove(inClause.Length - 1, 1); // Remove trailing comma
                }
                else
                {
                    inClause.Append("null");
                }

                StringBuilder q = new StringBuilder();
                q.AppendLine("select env_task.id, env_task.name, env_wfc_status.name, env_release.user_id");
                q.AppendLine("  from e.env_task, e.env_wfc_status, e.env_release, env_user");
                q.AppendLine(" where env_task.env_wfc_status_id = env_wfc_status.id");
                q.AppendLine("   and env_task.env_release_id = env_release.id");
                q.AppendLine("   and env_task.resp_env_user_id = env_user.id");
                q.AppendFormat("   and (env_task.id in ({0})", inClause);
                q.AppendLine();

                // Also include new tasks not already watched?
                if (settings.autoWatch)
                {
                    OracleParameter parUser = new OracleParameter();
                    parUser.OracleDbType = OracleDbType.Varchar2;
                    parUser.ParameterName = ":oracle_user";
                    parUser.Value = username.ToUpper();
                    cmd.Parameters.Add(parUser);

                    q.AppendFormat("    or env_user.oracle_user = {0}", parUser.ParameterName);
                    q.AppendLine();
                    q.AppendLine("   and env_task.env_wfc_status_id in (2018 /* Integration Test Running */, 2015 /* Ready to Integrate */)");
                }
                q.AppendLine(")");

                cmd.CommandText = q.ToString();
                Console.Write("Running query...");

                Debug.WriteLine(cmd.CommandText);
                OracleDataReader reader = cmd.ExecuteReader();

                Console.WriteLine("done");

                foreach (ViewQueryResponse<string, ADAITask>.Row row in queryTask.Result.Rows)
                {
                    taskList.Add(row.IncludedDoc);
                }

                while (reader.Read())
                {
                    ADAITask t = null;

                    if (taskList.Exists(m => m.TaskID == reader.GetInt32(0)))
                    {
                        t = queryTask.Result.Rows.Where(e => e.IncludedDoc.TaskID == reader.GetInt32(0)).Single().IncludedDoc;
                    }
                    else
                    {
                        t = new ADAITask(reader.GetInt32(0));
                        taskList.Add(t);
                    }
                    t.TaskName = reader.GetString(1);
                    t.TaskStatus = reader.GetString(2);
                    t.TaskRelease = reader.GetString(3);
                    t.LastUpdated = DateTime.Now;

                    Debug.WriteLine("Task ID: " + t.TaskID + ", Task Name: " + t.TaskName + ", Task Status: " + t.TaskStatus +
                        ", Task Release: " + t.TaskRelease);
                }

                reader.Close();
            }
            catch (OracleException e)
            {
                Console.WriteLine(e.Message);
                return;
            }
            finally
            {
                if (conn != null)
                {
                    conn.Close();
                }
            }

            MyCouchStore store = new MyCouchStore(couch);

            foreach (ADAITask t in taskList)
            {
                Console.Write("Updating doc for task " + t.TaskID + "...");
                Task<ADAITask> storeTask = store.StoreAsync<ADAITask>(t);
                storeTask.Wait();
                Console.WriteLine("done");
            }

            Console.WriteLine("Finished! Waiting for next polling interval...");

            //GetChangesRequest getChangesRequest = new GetChangesRequest
            //{
            //    Feed = ChangesFeed.Normal
            //};

            //Task<ChangesResponse> changesTask = store.Client.Changes.GetAsync(getChangesRequest);
            //changesTask.Wait();
            //changeObserver.SetIgnoreUntilSeqNr(changesTask.Result.LastSeq);
        }

        static IDbConnection ProxyDbConnection(ConnectionInfo connInfo)
        {
            return new ProxyDbConnection(connInfo);
        }
    }
}
