using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using MyCouch;
using MyCouch.Requests;
using Nito.AsyncEx;
using Newtonsoft.Json;
using System.Threading;

namespace taskwatcher_daemon
{
    class Program
    {
        const string DB = "dev1.avaloq";

        static Timer workTimer = null;
        static string connString = null;
        static string username = null;
        static string password = null;

        static void Main(string[] args)
        {
            AsyncContext.Run(() => MainAsync(args));
        }

        static async void MainAsync(string[] args)
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

            using (MyCouchClient couch = new MyCouchClient(connInfo))
            {
                // Create the database if it does not exist
                await couch.Database.PutAsync();

                //using (MyCouchStore store = new MyCouchStore(couch))
                //{
                //    while (true)
                //    {
                //        Console.Write("Enter task ID: ");
                //        string taskID = Console.ReadLine();

                //        ADAITask t = await store.GetByIdAsync<ADAITask>(taskID);

                //        Console.WriteLine("Got task document: " + t.TaskID);
                //        t.LastUpdated = DateTime.Now;

                //        Console.WriteLine("Storing doc...");
                //        DocumentHeader head = await store.StoreAsync(JsonConvert.SerializeObject(t).ToString());
                //        Console.WriteLine("Doc stored: " + head.Id);
                //    }
                //}

                MyCouchStore store = new MyCouchStore(couch);

                // Start timer - callback is called immediately and then every 10 minutes thereafter
                workTimer = new Timer(WorkTimerCallback, store, 0, 1 * 1000 * 60);

                //GetChangesRequest getChangesRequest = new GetChangesRequest
                //{
                //    Feed = ChangesFeed.Continuous
                //};

                //CancellationToken cancellationToken = new CancellationToken();
                //IObservable<string> changes = couch.Changes.ObserveContinuous(getChangesRequest, cancellationToken);

                //changes.Subscribe(this);

                while (true)
                {

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

        static async void WorkTimerCallback(object state)
        {
            MyCouchStore store = (MyCouchStore) state;

            Console.Write("Polling tasklist...");
            // Get all docs
            IEnumerable<Row<ADAITask>> rows = await store.QueryAsync<ADAITask>(new Query("_all_docs"));

            Console.WriteLine("done - " + rows.Count() + " tasks fetched");

            Console.Write("Connecting to " + DB + "...");

            OracleConnection conn = null;

            try
            {
                conn = new OracleConnection(connString);
                conn.Open();

                Console.WriteLine("done");

                string inClause = "";
                foreach (Row<ADAITask> row in rows)
                {
                    inClause = inClause + row.Value.TaskID + ",";
                }
                inClause = inClause.TrimEnd(',');

                OracleCommand cmd = new OracleCommand();
                cmd.Connection = conn;
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.CommandText = "select env_task.id, env_task.name, env_wfc_status.user_id, env_release.user_id " +
                                  "from e.env_task, e.env_wfc_status, e.env_release " +
                                  "where env_task.id in (" + inClause + ") " +
                                  "  and env_task.env_wfc_status_id = env_wfc_status.id " +
                                  "  and env_task.env_release_id = env_release.id";
                OracleDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    Row<ADAITask> t = rows.Where(e => e.Value.TaskID == reader.GetInt32(0)).Single();
                    t.Value.TaskName = reader.GetString(1);
                    t.Value.TaskStatus = reader.GetString(2);
                    t.Value.TaskRelease = reader.GetString(3);
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

            foreach (Row<ADAITask> row in rows)
            {
                
            }
        }
    }
}
