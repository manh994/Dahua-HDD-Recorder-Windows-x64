using System;
using System.IO;
using System.ServiceProcess;
using System.Timers;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using NetSDKCS;

namespace NvrRecorderService
{
    public partial class Service1 : ServiceBase
    {
        // Config
        private string Mode;
        private string NvrIp;
        private ushort NvrPort;
        private string Username;
        private string Password;
        private int[] Channels;
        private string[] CameraConfigs;

        private string SaveFolder;
        private int RecordStartHour;
        private int RecordEndHour;
        private int RotateMinutes;
        private int RetentionDays;
        private long MaxStorageBytes;
        private long MaxLogBytes;

        // SDK handles
        private IntPtr nvrLoginID = IntPtr.Zero;
        private Dictionary<int, ChannelRecorder> nvrRecorders = new Dictionary<int, ChannelRecorder>();
        private List<CameraRecorder> cameras = new List<CameraRecorder>();

        private System.Timers.Timer rotateTimer;
        private static fDisConnectCallBack disCb;
        private static fHaveReConnectCallBack recCb;

        public Service1()
        {
            InitializeComponent();
            ServiceName = "NvrRecorderService";
        }

        protected override void OnStart(string[] args)
        {
            LoadConfig();
            Directory.CreateDirectory(SaveFolder);

            disCb = new fDisConnectCallBack(OnDisconnect);
            recCb = new fHaveReConnectCallBack(OnReconnect);

            if (!NETClient.Init(disCb, IntPtr.Zero, null))
            {
                Log("Init failed: " + NETClient.GetLastError());
                Stop();
                return;
            }
            NETClient.SetAutoReconnect(recCb, IntPtr.Zero);

            if (Mode == "NVR") InitNvr();
            else if (Mode == "MultiCam") InitCameras();

            rotateTimer = new System.Timers.Timer(RotateMinutes * 60 * 1000);
            rotateTimer.Elapsed += (s, e) => RotateAll();
            rotateTimer.Start();
        }

        protected override void OnStop()
        {
            rotateTimer?.Stop();
            rotateTimer?.Dispose();

            StopAll();

            if (nvrLoginID != IntPtr.Zero)
            {
                NETClient.Logout(nvrLoginID);
                nvrLoginID = IntPtr.Zero;
            }

            NETClient.Cleanup();
            Log("Service stopped");
        }

        private void LoadConfig()
        {
            Mode = ConfigurationManager.AppSettings["Mode"];
            NvrIp = ConfigurationManager.AppSettings["NvrIp"];
            ushort.TryParse(ConfigurationManager.AppSettings["NvrPort"], out NvrPort);
            Username = ConfigurationManager.AppSettings["Username"];
            Password = ConfigurationManager.AppSettings["Password"];

            var chStr = ConfigurationManager.AppSettings["Channels"];
            if (!string.IsNullOrEmpty(chStr))
                Channels = chStr.Split(',').Select(x => int.Parse(x.Trim())).ToArray();

            var cams = ConfigurationManager.AppSettings["Cameras"];
            if (!string.IsNullOrEmpty(cams))
                CameraConfigs = cams.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            SaveFolder = ConfigurationManager.AppSettings["SaveFolder"];
            RecordStartHour = int.Parse(ConfigurationManager.AppSettings["RecordStartHour"]);
            RecordEndHour = int.Parse(ConfigurationManager.AppSettings["RecordEndHour"]);
            RotateMinutes = int.Parse(ConfigurationManager.AppSettings["RotateMinutes"]);
            RetentionDays = int.Parse(ConfigurationManager.AppSettings["RetentionDays"]);
            MaxStorageBytes = long.Parse(ConfigurationManager.AppSettings["MaxStorageGB"]) * 1024 * 1024 * 1024;
            MaxLogBytes = long.Parse(ConfigurationManager.AppSettings["MaxLogMB"]) * 1024 * 1024;
        }

        // ==================== NVR MODE =====================
        private void InitNvr()
        {
            var dev = new NET_DEVICEINFO_Ex();
            nvrLoginID = NETClient.LoginWithHighLevelSecurity(
                NvrIp, NvrPort, Username, Password,
                EM_LOGIN_SPAC_CAP_TYPE.TCP, IntPtr.Zero, ref dev);

            if (nvrLoginID == IntPtr.Zero)
            {
                Log("NVR login failed: " + NETClient.GetLastError());
                return;
            }

            Log($"NVR login success, channels={dev.nChanNum}");

            foreach (var ch in Channels)
            {
                var rec = new ChannelRecorder { Channel = ch };
                rec.RealHandle = NETClient.RealPlay(nvrLoginID, ch, IntPtr.Zero, EM_RealPlayType.EM_A_RType_Realplay);
                if (rec.RealHandle != IntPtr.Zero)
                {
                    nvrRecorders[ch] = rec;
                    StartSaving(rec, SaveFolder);
                }
                else Log($"RealPlay ch{ch} fail: {NETClient.GetLastError()}");
            }
        }

        // ==================== MULTICAM MODE =====================
        private void InitCameras()
        {
            foreach (var conf in CameraConfigs)
            {
                var parts = conf.Trim().Split(':');
                if (parts.Length < 5) continue;

                var cam = new CameraRecorder
                {
                    Ip = parts[0],
                    Port = ushort.Parse(parts[1]),
                    User = parts[2],
                    Pass = parts[3],
                    Channel = int.Parse(parts[4])
                };

                var dev = new NET_DEVICEINFO_Ex();
                cam.LoginID = NETClient.LoginWithHighLevelSecurity(
                    cam.Ip, cam.Port, cam.User, cam.Pass,
                    EM_LOGIN_SPAC_CAP_TYPE.TCP, IntPtr.Zero, ref dev);

                if (cam.LoginID != IntPtr.Zero)
                {
                    cam.RealHandle = NETClient.RealPlay(cam.LoginID, cam.Channel, IntPtr.Zero, EM_RealPlayType.EM_A_RType_Realplay);
                    if (cam.RealHandle != IntPtr.Zero)
                    {
                        cameras.Add(cam);
                        string folder = Path.Combine(SaveFolder, cam.Ip.Replace(".", "_"));
                        StartSaving(cam, folder);
                    }
                    else Log($"RealPlay {cam.Ip} fail: {NETClient.GetLastError()}");
                }
                else Log($"Login {cam.Ip} fail: {NETClient.GetLastError()}");
            }
        }

        // ==================== COMMON =====================
        private void StartSaving(ChannelRecorder rec, string folder)
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"ch{rec.Channel}_{DateTime.Now:yyyyMMdd_HHmmss}.dav");
            rec.IsSaving = NETClient.SaveRealData(rec.RealHandle, file);
            Log(rec.IsSaving ? $"Saving {file}" : $"Save fail ch{rec.Channel}: {NETClient.GetLastError()}");
        }

        private void StartSaving(CameraRecorder cam, string folder)
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"ch{cam.Channel}_{DateTime.Now:yyyyMMdd_HHmmss}.dav");
            cam.IsSaving = NETClient.SaveRealData(cam.RealHandle, file);
            Log(cam.IsSaving ? $"Saving {file}" : $"Save fail {cam.Ip}: {NETClient.GetLastError()}");
        }

        private void RotateAll()
        {
            var now = DateTime.Now;
            if (now.Hour < RecordStartHour || now.Hour >= RecordEndHour)
            {
                StopAll();
                return;
            }

            // NVR mode
            foreach (var rec in nvrRecorders.Values)
            {
                if (rec.IsSaving) { NETClient.StopSaveRealData(rec.RealHandle); rec.IsSaving = false; }
                StartSaving(rec, SaveFolder);
            }

            // MultiCam mode
            foreach (var cam in cameras)
            {
                if (cam.IsSaving) { NETClient.StopSaveRealData(cam.RealHandle); cam.IsSaving = false; }
                string folder = Path.Combine(SaveFolder, cam.Ip.Replace(".", "_"));
                StartSaving(cam, folder);
            }

            CleanupOldFiles();
            CleanupByStorageLimit();
            CleanupLogIfNeeded();
        }

        private void StopAll()
        {
            foreach (var rec in nvrRecorders.Values)
            {
                if (rec.IsSaving) { NETClient.StopSaveRealData(rec.RealHandle); rec.IsSaving = false; }
                if (rec.RealHandle != IntPtr.Zero) { NETClient.StopRealPlay(rec.RealHandle); rec.RealHandle = IntPtr.Zero; }
            }
            foreach (var cam in cameras)
            {
                if (cam.IsSaving) { NETClient.StopSaveRealData(cam.RealHandle); cam.IsSaving = false; }
                if (cam.RealHandle != IntPtr.Zero) { NETClient.StopRealPlay(cam.RealHandle); cam.RealHandle = IntPtr.Zero; }
                if (cam.LoginID != IntPtr.Zero) { NETClient.Logout(cam.LoginID); cam.LoginID = IntPtr.Zero; }
            }
        }

        private void CleanupOldFiles()
        {
            try
            {
                var files = Directory.GetFiles(SaveFolder, "*.dav", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays))
                    {
                        File.Delete(f);
                        Log("Deleted old file: " + f);
                    }
                }
            }
            catch (Exception ex) { Log("Cleanup error: " + ex.Message); }
        }

        private void CleanupByStorageLimit()
        {
            try
            {
                var files = new DirectoryInfo(SaveFolder).GetFiles("*.dav", SearchOption.AllDirectories)
                    .OrderBy(f => f.LastWriteTime).ToList();
                long totalSize = files.Sum(f => f.Length);
                while (totalSize > MaxStorageBytes && files.Count > 0)
                {
                    var oldest = files[0];
                    File.Delete(oldest.FullName);
                    Log("Deleted (storage limit) " + oldest.FullName);
                    totalSize -= oldest.Length;
                    files.RemoveAt(0);
                }
            }
            catch (Exception ex) { Log("Storage cleanup error: " + ex.Message); }
        }

        private void CleanupLogIfNeeded()
        {
            try
            {
                string logFile = Path.Combine(SaveFolder, "service.log");
                if (File.Exists(logFile))
                {
                    FileInfo fi = new FileInfo(logFile);
                    if (fi.Length > MaxLogBytes)
                    {
                        File.Delete(logFile);
                        File.AppendAllText(logFile, $"[{DateTime.Now}] Log reset due to size\r\n");
                    }
                }
            }
            catch { }
        }

        private void OnDisconnect(IntPtr lLoginID, IntPtr ip, int port, IntPtr user) => Log("Disconnected");
        private void OnReconnect(IntPtr lLoginID, IntPtr ip, int port, IntPtr user) => Log("Reconnected");

        private void Log(string msg)
        {
            string logFile = Path.Combine(SaveFolder, "service.log");
            File.AppendAllText(logFile, $"[{DateTime.Now}] {msg}\r\n");
        }
    }

    class ChannelRecorder
    {
        public int Channel { get; set; }
        public IntPtr RealHandle { get; set; } = IntPtr.Zero;
        public bool IsSaving { get; set; } = false;
    }

    class CameraRecorder
    {
        public string Ip { get; set; }
        public ushort Port { get; set; }
        public string User { get; set; }
        public string Pass { get; set; }
        public int Channel { get; set; }
        public IntPtr LoginID { get; set; } = IntPtr.Zero;
        public IntPtr RealHandle { get; set; } = IntPtr.Zero;
        public bool IsSaving { get; set; } = false;
    }
}