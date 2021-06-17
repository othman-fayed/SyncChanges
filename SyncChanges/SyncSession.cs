using Newtonsoft.Json;
using System.IO;

namespace SyncChanges
{
    public class SyncSession
    {
        const string CURRENT_SESSION_FILENAME = "current_session.json";

        public bool InProgress { get; internal set; }
        public string DestinationName { get; internal set; }

        public static SyncSession LoadSessionFromFile()
        {
            try
            {
                return JsonConvert.DeserializeObject<SyncSession>(File.ReadAllText(CURRENT_SESSION_FILENAME));
            }
            catch (System.Exception)
            {
                return new SyncSession();
            }
        }

        public static void SaveSessionToFile(SyncSession syncSession)
        {
            var json = JsonConvert.SerializeObject(syncSession);
            File.WriteAllText(CURRENT_SESSION_FILENAME, json);
        }
    }
}