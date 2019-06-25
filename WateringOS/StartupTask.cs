using System;
using System.IO;
using System.Text;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SqlClient;
using System.Globalization;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.I2c;
using Windows.Devices.Gpio;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using Windows.Networking.Sockets;
using Windows.Foundation.Diagnostics;
using Windows.ApplicationModel.Background;
using System.Net.Sockets;
using System.Net;
using Windows.Storage;

/*
    2018/06/23
        Fresh start with I2C initialisation and LM75 CPU temperature measurement
        Added GPIO
        Added ATmega
        Added TCP HTML
    2018/09/22
        Added SQL communication for Logging and Signals
        Changed DebugLog structure for more details
    2018/09/23
        Fixed SQL communication
        Debug and Sensor Log integrated
        SQL tested -> OK
        Added Application setting save and load
        Added TCP Parameter modification
    2019/01/17
        Debug output/log for parameter values loaded
        Added water timing settings to UDP stream
        TCP api command and parameter logged
	2019/01/30
		Added Exposed Temperature to UDP stream
		Adopted Delphi UDP stream and TCP commands to C# code
    2019/04/13 
        Added time check in LogTimer to check for Watering condition - Logged in SQL  
    2019/05/06
        Changed I2C speed to standard (400->100kHz)
        Changed SQLWrite() to writ from global strings and if not busy
    2019/05/07
        Read temperatures only every eg 10 times
        Use last value in case of sensor failure
        Corrected parameter store and load
        Tested activation of watering times
    2019/05/07
        Added SQLActive condition and reconnect before ExecuteNonQuery()
        Changed SQL logging to Batch mode (continous string)
    2019/06/05
        Changed Error Message names to be better sorted in SQL PowerBI
        Changed to NOT in debug
    2019/06/25
        Added local logging to *.txt
        Added Rain and Ground factors on watering volume
*/

namespace WateringOS
{
    static class GlobalAttributes
    {
        public static string LocalLogPath = DateTime.Now.Year.ToString("yyyy_MM_dd__HH_mm_ss") + " - Log.txt";

        public static bool   SqlBusy = false;
        public static string vSqlDat_Prefix  = "INSERT INTO Signals(DateTime, Flow1, Flow2, Flow3, Flow4, Flow5, Rain, Ground, TankLevel, Pressure, TempCPU, TempAmb, TempExp, Pump, Valve1, Valve2, Valve3, Valve4, Valve5, TEST) VALUES ";
        public static string vSqlDat_Data    = "";
        public static string vSqlLog_Prefix  = "INSERT INTO Log(TimeStamp, Instance, Type, Name, Details, TEST) VALUES ";
        public static string vSqlLog_Data    = "";

        public static byte cnt_TempAir = 60;
        public static byte cnt_TempCPU = 60;
        public static byte cnt_TankLvl = 60;

        public static byte v_Flow1    = 0;
        public static byte v_Flow2    = 0;
        public static byte v_Flow3    = 0;
        public static byte v_Flow4    = 0;
        public static byte v_Flow5    = 0;
        public static byte v_Rain     = 0;
        public static byte v_Ground   = 0;
        public static byte v_Sens     = 0;
        public static byte v_IOs      = 0;
        public static byte v_Level    = 0;
        public static byte v_Pressure = 0;
        public static byte v_CPUTemp  = 0;
        public static byte v_AirTemp  = 0;
        public static byte v_ExpTemp  = 0;

    }

    public delegate string TcpRequestReceived(string request); // Basic Server which listens for TCP Requests and provides the user with the ability to craft own responses as strings
    public sealed class TcpServer
    {
        private StreamSocketListener fListener;
        private const uint BUFFER_SIZE = 8192;
        public TcpRequestReceived RequestReceived { get; set; }
        public TcpServer() { }
        public async void Initialise(int port)
        {
            this.fListener = new StreamSocketListener();
            await this.fListener.BindServiceNameAsync(port.ToString());
            this.fListener.ConnectionReceived += (sender, args) =>
            {
                HandleRequest(sender, args);
            };
        }
        private async void HandleRequest(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StringBuilder request = new StringBuilder();
            using (IInputStream input = args.Socket.InputStream)
            {
                byte[] data = new byte[BUFFER_SIZE];
                IBuffer buffer = data.AsBuffer();
                uint dataRead = BUFFER_SIZE;
                while (dataRead == BUFFER_SIZE)
                {
                    await input.ReadAsync(buffer, BUFFER_SIZE, InputStreamOptions.Partial);
                    request.Append(Encoding.UTF8.GetString(data, 0, data.Length));
                    dataRead = buffer.Length;
                }
            }
            string requestString = request.ToString();
            string response = RequestReceived?.Invoke(requestString);
            using (IOutputStream output = args.Socket.OutputStream)
            using (Stream responseStream = output.AsStreamForWrite())
            {
                MemoryStream body;
                if (response != null)
                {
                    body = new MemoryStream(Encoding.UTF8.GetBytes(response));
                }
                else
                {
                    body = new MemoryStream(Encoding.UTF8.GetBytes("No response specified"));
                }
                var header = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/xml\r\nConnection: close\r\n\r\n");
                await responseStream.WriteAsync(header, 0, header.Length);
                await body.CopyToAsync(responseStream);
                await responseStream.FlushAsync();
            }
        }
    }
    public sealed class TwiServer
    {
        private I2cDevice TWI_TempCPU;      // 0x48
        private I2cDevice TWI_TempAmbient;  // 0x4B
        private I2cDevice TWI_uController;  // 0x56
        private I2cConnectionSettings settings1;
        private I2cConnectionSettings settings2;
        private I2cConnectionSettings settings3;
        private I2cController controller;
        public StartupTask vParent { get; set; }

        public async void InitTWIAsync()
        {
            vParent.DebugLog("[TWI]", "Information", "Start initialisation", "The intialization of the TWI communication class has started.");

            try
            {
                this.controller = await I2cController.GetDefaultAsync(); // Create an I2cDevice with our selected bus controller and I2C settings

                try
                {
                    this.settings1 = new I2cConnectionSettings(0x48) { BusSpeed = I2cBusSpeed.StandardMode };   // CPU Temperature
                    this.TWI_TempCPU = this.controller.GetDevice(settings1);
                    this.TWI_TempCPU.ConnectionSettings.SharingMode = I2cSharingMode.Shared;
                }
                catch (Exception e)
                {
                    vParent.DebugLog("[TWI]", "Error", "TempCPU Initializing", e.Message);
                }

                try
                {
                    this.settings2 = new I2cConnectionSettings(0x4F) { BusSpeed = I2cBusSpeed.StandardMode };   // Ambient Temperature
                    this.TWI_TempAmbient = this.controller.GetDevice(settings2);
                    this.TWI_TempAmbient.ConnectionSettings.SharingMode = I2cSharingMode.Shared;
                }
                catch (Exception e)
                {
                    vParent.DebugLog("[TWI]", "Error", "TempAmb Initializing" , e.Message);
                }

                try
                {
                    this.settings3 = new I2cConnectionSettings(0x56) { BusSpeed = I2cBusSpeed.StandardMode };   // ATmega uController
                    this.TWI_uController = this.controller.GetDevice(settings3);
                    this.TWI_uController.ConnectionSettings.SharingMode = I2cSharingMode.Shared;
                }
                catch (Exception e)
                {
                    vParent.DebugLog("[TWI]", "Error", "ATmega uC Initializing" , e.Message);
                }
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI Class Initializing" , e.Message);
            }
        }
        // ----- ATmega Commands
        public void TWI_ATmega_ResetCounter()
        {
            try
            {
                var vARC = new byte[] { 0x40 };
                this.TWI_uController.Write(vARC);
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ResetCounter", e.Message);
            }
        }
        public byte TWI_ATmega_ReadFlow(byte vChn)
        {
            try
            {
                var vASr = new byte[] { (byte)(32 + vChn) };    // 0x20 ... 0x28
                var vASa = new byte[1];
                this.TWI_uController.Write(vASr);
                this.TWI_uController.Read(vASa);
                return vASa[0];
            }
            catch (Exception e)
            {
                switch (vChn)
                {
                    case 0:  vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadFlow" , "TWI Atmega Flow 1" + e.Message); return GlobalAttributes.v_Flow1; 
                    case 1:  vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadFlow" , "TWI Atmega Flow 2" + e.Message); return GlobalAttributes.v_Flow2;
                    case 2:  vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadFlow" , "TWI Atmega Flow 3" + e.Message); return GlobalAttributes.v_Flow3; 
                    case 3:  vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadFlow" , "TWI Atmega Flow 4" + e.Message); return GlobalAttributes.v_Flow4; 
                    case 4:  vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadFlow" , "TWI Atmega Flow 5" + e.Message); return GlobalAttributes.v_Flow5; 
                    default: return 0;
                }
            }
        }
        public byte TWI_ATmega_ReadPressure()
        {
            try
            {
                var vASr = new byte[] { 0x27 };    // Channel 2 = pressure
                var vASa = new byte[1];
                this.TWI_uController.Write(vASr);
                this.TWI_uController.Read(vASa);
                return vASa[0];
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadPressure" , e.Message);
                return GlobalAttributes.v_Pressure;
            }
        }
        public byte TWI_ATmega_ReadRain()
        {
            try
            {
                var vASr = new byte[] { 0x25 };    // Channel 0 = Rain
                var vASa = new byte[1];
                this.TWI_uController.Write(vASr);
                this.TWI_uController.Read(vASa);
                return vASa[0];
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadRain" , e.Message);
                return GlobalAttributes.v_Rain;
            }
        }
        public byte TWI_ATmega_ReadLevel()
        {
            try
            {
                var vASr = new byte[] { 0x26 };    // Channel 1 = Level
                var vASa = new byte[1];
                this.TWI_uController.Write(vASr);
                this.TWI_uController.Read(vASa);
                return vASa[0];
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadLevel" , e.Message);
                return GlobalAttributes.v_Level;
            }
        }
        public byte TWI_ATmega_ReadGround()
        {
            try
            {
                var vASr = new byte[] { 0x28 };    // Channel 3 = Soil Humidity
                var vASa = new byte[1];
                this.TWI_uController.Write(vASr);
                this.TWI_uController.Read(vASa);
                return vASa[0];
            }
            catch (Exception e)
            {
                vParent.DebugLog("[TWI]", "Error", "TWI_ATmega_ReadGround" , e.Message);
                return GlobalAttributes.v_Ground;
            }
        }
        // ----- Temperature Sensor Command
        public Int16 TWI_CPUTemp_Read()
        {
            if (GlobalAttributes.cnt_TempCPU >= 10)
            {
                GlobalAttributes.cnt_TempCPU = 0;
                try
                {
                    var vASr = new byte[] { 0x00 };    // 0x00 = Read Temp
                    var vASa = new byte[2];
                    this.TWI_TempCPU.Write(vASr);
                    this.TWI_TempCPU.Read(vASa);
                    var vNeg = 1;
                    if ((vASa[0] & 128) == 1) { vNeg = -1; }
                    int tTcpu = (vASa[0] << 8) + vASa[1];
                    return Convert.ToInt16(((tTcpu >> 7) & 255) * vNeg);
                }
                catch (Exception e)
                {
                    vParent.DebugLog("[TWI]", "Error", "TWI_CPUTemp_Read" , e.Message);
                    return GlobalAttributes.v_CPUTemp;
                }
            }
            else { GlobalAttributes.cnt_TempCPU++; return GlobalAttributes.v_CPUTemp; }
        }
        public Int16 TWI_AirTemp_Read()
        {
            if (GlobalAttributes.cnt_TempAir >= 60)
            {
                GlobalAttributes.cnt_TempAir = 0;
                try
                {
                    var vASr = new byte[] { 0x00 };    // 0x00 = Read Temp
                    var vASa = new byte[2];
                    this.TWI_TempAmbient.Write(vASr);
                    this.TWI_TempAmbient.Read(vASa);
                    var vNeg = 1;
                    if ((vASa[0] & 128) == 1) { vNeg = -1; }
                    return Convert.ToInt16((vASa[0] & 127) * vNeg);
                }
                catch (Exception e)
                {
                    vParent.DebugLog("[TWI]", "Error", "TWI_AirTemp_Read" , e.Message);
                    return GlobalAttributes.v_AirTemp;
                }
            }
            else { GlobalAttributes.cnt_TempAir++; return GlobalAttributes.v_AirTemp; }
        }

    }
    public sealed class GpioServer
    {
        private GpioPin DO_Pump;
        private GpioPin DO_Valve1;
        private GpioPin DO_Valve2;
        private GpioPin DO_Valve3;
        private GpioPin DO_Valve4;
        private GpioPin DO_Valve5;
        private GpioPin DI_PF5;
        private GpioPin DI_PF12;
        private GpioPin DI_PF24;
        private GpioController gpio;
        public StartupTask vParent { get; set; }

        public void InitGPIO()
        {
            vParent.DebugLog("[DIO]", "Information", "Start initialisation", "The intialization of the Raspberry GPIO communication class has started.");
            try
            {
                this.gpio = GpioController.GetDefault();

                this.DO_Pump = this.gpio.OpenPin(18);
                this.DO_Valve1 = this.gpio.OpenPin(23);
                this.DO_Valve2 = this.gpio.OpenPin(24);
                this.DO_Valve3 = this.gpio.OpenPin(25);
                this.DO_Valve4 = this.gpio.OpenPin(12);
                this.DO_Valve5 = this.gpio.OpenPin(16);
                this.DI_PF5 = this.gpio.OpenPin(6);
                this.DI_PF12 = this.gpio.OpenPin(5);
                this.DI_PF24 = this.gpio.OpenPin(4);


                this.DO_Pump.SetDriveMode(GpioPinDriveMode.Output);
                this.DO_Valve1.SetDriveMode(GpioPinDriveMode.Output);
                this.DO_Valve2.SetDriveMode(GpioPinDriveMode.Output);
                this.DO_Valve3.SetDriveMode(GpioPinDriveMode.Output);
                this.DO_Valve4.SetDriveMode(GpioPinDriveMode.Output);
                this.DO_Valve5.SetDriveMode(GpioPinDriveMode.Output);
                this.DI_PF5.SetDriveMode(GpioPinDriveMode.Input);
                this.DI_PF12.SetDriveMode(GpioPinDriveMode.Input);
                this.DI_PF24.SetDriveMode(GpioPinDriveMode.Input);

                this.DO_Pump.Write(GpioPinValue.Low);
                this.DO_Valve1.Write(GpioPinValue.Low);
                this.DO_Valve2.Write(GpioPinValue.Low);
                this.DO_Valve3.Write(GpioPinValue.Low);
                this.DO_Valve4.Write(GpioPinValue.Low);
                this.DO_Valve5.Write(GpioPinValue.Low);
            }
            catch (Exception e)
            {
                vParent.DebugLog("[DIO]", "Error", "GPIO Class Initialization" , e.Message);
            }
        }
        public bool GetPinState(byte vPin)
        {
            try
            {
                this.gpio = GpioController.GetDefault();
                switch (vPin)
                {
                    case 1: if (this.DO_Pump.Read()   == GpioPinValue.High) { return true; } else { return false; }
                    case 2: if (this.DO_Valve1.Read() == GpioPinValue.High) { return true; } else { return false; }
                    case 3: if (this.DO_Valve2.Read() == GpioPinValue.High) { return true; } else { return false; }
                    case 4: if (this.DO_Valve3.Read() == GpioPinValue.High) { return true; } else { return false; }
                    case 5: if (this.DO_Valve4.Read() == GpioPinValue.High) { return true; } else { return false; }
                    case 6: if (this.DO_Valve5.Read() == GpioPinValue.High) { return true; } else { return false; }
                    case 7: if (this.DI_PF5.Read()    == GpioPinValue.High) { return true; } else { return false; }
                    case 8: if (this.DI_PF12.Read()   == GpioPinValue.High) { return true; } else { return false; }
                    case 9: if (this.DI_PF24.Read()   == GpioPinValue.High) { return true; } else { return false; }
                    default: return false;
                }
            }
            catch (Exception e)
            {
                vParent.DebugLog("[DIO]", "Error", "GetPinState" , e.Message);
                return false;
            }

        }
        public void SetPinState(byte vPin, bool vValue)
        {
            try
            {
                this.gpio = GpioController.GetDefault();
                switch (vPin)
                {
                    case 1: if (vValue) { this.DO_Pump.Write(GpioPinValue.High); break; } else { this.DO_Pump.Write(GpioPinValue.Low); break; }
                    case 2: if (vValue) { this.DO_Valve1.Write(GpioPinValue.High); break; } else { this.DO_Valve1.Write(GpioPinValue.Low); break; }
                    case 3: if (vValue) { this.DO_Valve2.Write(GpioPinValue.High); break; } else { this.DO_Valve2.Write(GpioPinValue.Low); break; }
                    case 4: if (vValue) { this.DO_Valve3.Write(GpioPinValue.High); break; } else { this.DO_Valve3.Write(GpioPinValue.Low); break; }
                    case 5: if (vValue) { this.DO_Valve4.Write(GpioPinValue.High); break; } else { this.DO_Valve4.Write(GpioPinValue.Low); break; }
                    case 6: if (vValue) { this.DO_Valve5.Write(GpioPinValue.High); break; } else { this.DO_Valve5.Write(GpioPinValue.Low); break; }
                    default: { break; }
                }
            }
            catch (Exception e)
            {
                vParent.DebugLog("[DIO]", "Error", "SetPinState" , e.Message);
            }
        }
    }
    public sealed class SqlServer
    {
        private SqlConnection SqlActiveConnection;
        public StartupTask vParent { get; set; }
        public void InitSql()
        {
            Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] Start initialisation");
            try
            {
                SqlActiveConnection = new SqlConnection("Data Source=wateringsystem.database.windows.net;Initial Catalog=WateringSystem;Persist Security Info=True;User ID=kollmeyer.michael;Password=00380465*Watering");
                SqlActiveConnection.Open();
            }
            catch (SqlException e)
            {
                Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] Error in SQL Class: " + e.Message);
            }
        }
        public void WriteToSql()
        {  
            if (SqlActiveConnection.State == ConnectionState.Open)
            {
                try
                {
                    if (GlobalAttributes.SqlBusy == false)
                    {
                        GlobalAttributes.SqlBusy = true;
                        if (GlobalAttributes.vSqlDat_Data != "")
                        {
                            string vSQLcommand_Dat = GlobalAttributes.vSqlDat_Prefix + GlobalAttributes.vSqlDat_Data.Remove(GlobalAttributes.vSqlDat_Data.Length - 1) + ";";
                            //Debug.WriteLine(vSQLcommand_Dat);
                            using (var command = new SqlCommand(vSQLcommand_Dat, SqlActiveConnection))
                            {
                                command.ExecuteNonQuery();
                            }
                        }
                        if (GlobalAttributes.vSqlLog_Data != "")
                        {
                            string vSQLcommand_Log = GlobalAttributes.vSqlLog_Prefix + GlobalAttributes.vSqlLog_Data.Remove(GlobalAttributes.vSqlLog_Data.Length - 1) + ";";
                            //Debug.WriteLine(vSQLcommand_Log);
                            using (var command = new SqlCommand(vSQLcommand_Log, SqlActiveConnection))
                            {
                                command.ExecuteNonQuery();
                            }
                        }
                        GlobalAttributes.vSqlDat_Data = "";
                        GlobalAttributes.vSqlLog_Data = "";
                        GlobalAttributes.SqlBusy = false;

                    }
                    else
                    {
                        Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] WriteToSql: SQL was called in busy state. Command dropped.");
                    }
                }
                catch (SqlException e)
                {
                    Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] WriteToSql: " + e.Message);
                    GlobalAttributes.SqlBusy = false;
                }
            }
            else
            {
                Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] No connection to Server. Try reconnecting...");
                try { SqlActiveConnection.Open(); } catch (SqlException e) { Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[SQL] " + e.Message); }
            }
        }
    }

    public sealed class StartupTask : IBackgroundTask
    {
        #region Variables
        byte cVOL1;
        byte cVOL2;
        byte cVOL3;
        byte cVOL4;
        byte cVOL5;

        byte cRAF1;
        byte cRAF2;
        byte cRAF3;
        byte cRAF4;
        byte cRAF5;

        byte cGAF1;
        byte cGAF2;
        byte cGAF3;
        byte cGAF4;
        byte cGAF5;

        bool cMOR1;
        bool cMOR2;
        bool cMOR3;
        bool cMOR4;
        bool cMOR5;

        bool cNOO1;
        bool cNOO2;
        bool cNOO3;
        bool cNOO4;
        bool cNOO5;

        bool cEVE1;
        bool cEVE2;
        bool cEVE3;
        bool cEVE4;
        bool cEVE5;

        bool cMON1;
        bool cMON2;
        bool cMON3;
        bool cMON4;
        bool cMON5;

        bool cTUE1;
        bool cTUE2;
        bool cTUE3;
        bool cTUE4;
        bool cTUE5;

        bool cWED1;
        bool cWED2;
        bool cWED3;
        bool cWED4;
        bool cWED5;

        bool cTHU1;
        bool cTHU2;
        bool cTHU3;
        bool cTHU4;
        bool cTHU5;

        bool cFRI1;
        bool cFRI2;
        bool cFRI3;
        bool cFRI4;
        bool cFRI5;

        bool cSAT1;
        bool cSAT2;
        bool cSAT3;
        bool cSAT4;
        bool cSAT5;

        bool cSUN1;
        bool cSUN2;
        bool cSUN3;
        bool cSUN4;
        bool cSUN5;
        #endregion Variables
        bool AppInDebug = false;

        private BackgroundTaskDeferral fDef;
        private TcpServer fTcpServer;
        private TwiServer fTwiServer;
        private GpioServer fGpioServer;
        private SqlServer fSqlServer;
        private Timer LogTimer;

        UdpClient TxUDPclient = new UdpClient();
        IPEndPoint IPconf = new IPEndPoint(IPAddress.Broadcast, 12300);

        public void DebugLog(string vInstance, string vType, string vMsg, string vDetail)
        {
            vMsg = vMsg.Replace("\r\n", " ");
            vMsg = vMsg.Replace("\n",   " ");
            vMsg = vMsg.Replace("\r",   " ");
            Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + vInstance + " " + vMsg);

            if (File.Exists(GlobalAttributes.LocalLogPath))
            {
                using (StreamWriter LogFile = File.AppendText(GlobalAttributes.LocalLogPath))
                { LogFile.WriteLine(DateTime.Now.ToString("o", CultureInfo.CurrentCulture) + ";" + vType + ";" +vInstance + ";" + vMsg); }
            }
            else
            {
                GlobalAttributes.LocalLogPath = DateTime.Now.Year.ToString("yyyy_MM_dd__HH_mm_ss") + " - Log.txt";
                using (StreamWriter LogFile = File.CreateText(GlobalAttributes.LocalLogPath))
                {
                    Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[APP] File not found - creating new local log file (" + GlobalAttributes.LocalLogPath + ")");
                    LogFile.WriteLine("WateringOS_0_4");
                    LogFile.WriteLine("(C)2019 by Michael Kollmeyer");
                    LogFile.WriteLine("");
                    LogFile.WriteLine("Date;Type;Function;Name;Detail");
                    LogFile.WriteLine(DateTime.Now.ToString("o", CultureInfo.CurrentCulture) + ";" + vType + ";" + vInstance + ";" + vMsg);
                }
            }

            GlobalAttributes.vSqlLog_Data += String.Format("('{0}', '{1}', '{2}', '{3}', '{4}', '{5}'),", DateTime.Now.ToString("o", CultureInfo.CurrentCulture), vInstance, vType, vMsg, vDetail, AppInDebug);
        }
        private void LogTimer_Tick(Object stateInfo)
        {
            #region variables
            GlobalAttributes.v_Flow1 = fTwiServer.TWI_ATmega_ReadFlow(0);
            GlobalAttributes.v_Flow2 = fTwiServer.TWI_ATmega_ReadFlow(1);
            GlobalAttributes.v_Flow3 = fTwiServer.TWI_ATmega_ReadFlow(2);
            GlobalAttributes.v_Flow4 = fTwiServer.TWI_ATmega_ReadFlow(3);
            GlobalAttributes.v_Flow5 = fTwiServer.TWI_ATmega_ReadFlow(4);
            GlobalAttributes.v_Rain = fTwiServer.TWI_ATmega_ReadRain();
            GlobalAttributes.v_Ground = fTwiServer.TWI_ATmega_ReadGround();
            GlobalAttributes.v_Sens = 0;
            GlobalAttributes.v_IOs = 0;
            if (GlobalAttributes.v_Rain < 100)   { GlobalAttributes.v_Sens |= 0x01; }  // Rain sensor not OK
            if (GlobalAttributes.v_Rain > 200)   { GlobalAttributes.v_Sens |= 0x02; }  // Rain
            if (GlobalAttributes.v_Ground < 100) { GlobalAttributes.v_Sens |= 0x04; }  // Ground sensor not OK
            if (GlobalAttributes.v_Ground > 200) { GlobalAttributes.v_Sens |= 0x08; }  // Ground damp

            if (!fGpioServer.GetPinState(7)) { GlobalAttributes.v_Sens |= 0x10; }  //  5V OK
            if (!fGpioServer.GetPinState(8)) { GlobalAttributes.v_Sens |= 0x20; }  // 12V OK
            if (!fGpioServer.GetPinState(9)) { GlobalAttributes.v_Sens |= 0x40; }  // 24V OK

            if (fGpioServer.GetPinState(1)) { GlobalAttributes.v_IOs |= 0x01; }    // Pump activated
            if (fGpioServer.GetPinState(2)) { GlobalAttributes.v_IOs |= 0x02; }    // Valve 1 activated
            if (fGpioServer.GetPinState(3)) { GlobalAttributes.v_IOs |= 0x04; }    // Valve 2 activated
            if (fGpioServer.GetPinState(4)) { GlobalAttributes.v_IOs |= 0x08; }    // Valve 3 activated
            if (fGpioServer.GetPinState(5)) { GlobalAttributes.v_IOs |= 0x10; }    // Valve 4 activated
            if (fGpioServer.GetPinState(6)) { GlobalAttributes.v_IOs |= 0x20; }    // Valve 5 activated

            GlobalAttributes.v_Level = fTwiServer.TWI_ATmega_ReadLevel();
            GlobalAttributes.v_Pressure = fTwiServer.TWI_ATmega_ReadPressure();
            GlobalAttributes.v_CPUTemp = Convert.ToByte(fTwiServer.TWI_CPUTemp_Read());
            GlobalAttributes.v_AirTemp = Convert.ToByte(fTwiServer.TWI_AirTemp_Read());
            GlobalAttributes.v_ExpTemp = Convert.ToByte(27);  // replacement value

            byte v_Morning = 0; // 1bit per Output b0~b4 = Valve 1 ~ Valve 5
            byte v_Noon = 0; // >>
            byte v_Evening = 0;
            byte v_Monday = 0;
            byte v_Tuesday = 0;
            byte v_Wednesday = 0;
            byte v_Thursday = 0;
            byte v_Friday = 0;
            byte v_Saturday = 0;
            byte v_Sunday = 0;

            if (cMOR1) { v_Morning |= 0x01; }
            if (cMOR2) { v_Morning |= 0x02; }
            if (cMOR3) { v_Morning |= 0x04; }
            if (cMOR4) { v_Morning |= 0x08; }
            if (cMOR5) { v_Morning |= 0x10; }

            if (cNOO1) { v_Noon |= 0x01; }
            if (cNOO2) { v_Noon |= 0x02; }
            if (cNOO3) { v_Noon |= 0x04; }
            if (cNOO4) { v_Noon |= 0x08; }
            if (cNOO5) { v_Noon |= 0x10; }

            if (cEVE1) { v_Evening |= 0x01; }
            if (cEVE2) { v_Evening |= 0x02; }
            if (cEVE3) { v_Evening |= 0x04; }
            if (cEVE4) { v_Evening |= 0x08; }
            if (cEVE5) { v_Evening |= 0x10; }

            if (cMON1) { v_Monday |= 0x01; }
            if (cMON2) { v_Monday |= 0x02; }
            if (cMON3) { v_Monday |= 0x04; }
            if (cMON4) { v_Monday |= 0x08; }
            if (cMON5) { v_Monday |= 0x10; }

            if (cTUE1) { v_Tuesday |= 0x01; }
            if (cTUE2) { v_Tuesday |= 0x02; }
            if (cTUE3) { v_Tuesday |= 0x04; }
            if (cTUE4) { v_Tuesday |= 0x08; }
            if (cTUE5) { v_Tuesday |= 0x10; }

            if (cWED1) { v_Wednesday |= 0x01; }
            if (cWED2) { v_Wednesday |= 0x02; }
            if (cWED3) { v_Wednesday |= 0x04; }
            if (cWED4) { v_Wednesday |= 0x08; }
            if (cWED5) { v_Wednesday |= 0x10; }

            if (cTHU1) { v_Thursday |= 0x01; }
            if (cTHU2) { v_Thursday |= 0x02; }
            if (cTHU3) { v_Thursday |= 0x04; }
            if (cTHU4) { v_Thursday |= 0x08; }
            if (cTHU5) { v_Thursday |= 0x10; }

            if (cFRI1) { v_Friday |= 0x01; }
            if (cFRI2) { v_Friday |= 0x02; }
            if (cFRI3) { v_Friday |= 0x04; }
            if (cFRI4) { v_Friday |= 0x08; }
            if (cFRI5) { v_Friday |= 0x10; }

            if (cSAT1) { v_Saturday |= 0x01; }
            if (cSAT2) { v_Saturday |= 0x02; }
            if (cSAT3) { v_Saturday |= 0x04; }
            if (cSAT4) { v_Saturday |= 0x08; }
            if (cSAT5) { v_Saturday |= 0x10; }

            if (cSUN1) { v_Sunday |= 0x01; }
            if (cSUN2) { v_Sunday |= 0x02; }
            if (cSUN3) { v_Sunday |= 0x04; }
            if (cSUN4) { v_Sunday |= 0x08; }
            if (cSUN5) { v_Sunday |= 0x10; }

            #endregion variables
            #region UDPstream
            List<byte> vDataStream = new List<byte>();
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Flow1));    // 0
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Flow2));    // 2
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Flow3));    // 4
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Flow4));    // 6
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Flow5));    // 8

            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Sens));     //10
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_IOs));      //12

            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Level));    //14
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_Pressure)); //16
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_CPUTemp));  //18
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_AirTemp));  //20
            vDataStream.AddRange(BitConverter.GetBytes(GlobalAttributes.v_ExpTemp));  //22

            vDataStream.AddRange(BitConverter.GetBytes(cVOL1));		 //24
            vDataStream.AddRange(BitConverter.GetBytes(cVOL2));		 //26
            vDataStream.AddRange(BitConverter.GetBytes(cVOL3));		 //28
            vDataStream.AddRange(BitConverter.GetBytes(cVOL4));		 //30
            vDataStream.AddRange(BitConverter.GetBytes(cVOL5));		 //32

            vDataStream.AddRange(BitConverter.GetBytes(cRAF1));		 //34
            vDataStream.AddRange(BitConverter.GetBytes(cRAF2));		 //36
            vDataStream.AddRange(BitConverter.GetBytes(cRAF3));		 //38
            vDataStream.AddRange(BitConverter.GetBytes(cRAF4));		 //40
            vDataStream.AddRange(BitConverter.GetBytes(cRAF5));      //42

            vDataStream.AddRange(BitConverter.GetBytes(cGAF1));		 //44
            vDataStream.AddRange(BitConverter.GetBytes(cGAF2));		 //46
            vDataStream.AddRange(BitConverter.GetBytes(cGAF3));		 //48
            vDataStream.AddRange(BitConverter.GetBytes(cGAF4));		 //50
            vDataStream.AddRange(BitConverter.GetBytes(cGAF5));		 //52

            vDataStream.AddRange(BitConverter.GetBytes(v_Morning));	 //54
            vDataStream.AddRange(BitConverter.GetBytes(v_Noon));	 //56
            vDataStream.AddRange(BitConverter.GetBytes(v_Evening));	 //58
            vDataStream.AddRange(BitConverter.GetBytes(v_Monday));	 //60
            vDataStream.AddRange(BitConverter.GetBytes(v_Tuesday));	 //62
            vDataStream.AddRange(BitConverter.GetBytes(v_Wednesday));//64
            vDataStream.AddRange(BitConverter.GetBytes(v_Thursday)); //66
            vDataStream.AddRange(BitConverter.GetBytes(v_Friday));	 //68
            vDataStream.AddRange(BitConverter.GetBytes(v_Saturday)); //70
            vDataStream.AddRange(BitConverter.GetBytes(v_Sunday));	 //72

            var UDPsend = Task.Run(async delegate { await TxUDPclient.SendAsync(vDataStream.ToArray(), vDataStream.ToArray().Length, IPconf); });
            UDPsend.Wait();

            #endregion UDPstream
            #region SQLlogger
            string SignalSqlString = String.Format(
                "('{0}', '{1}', '{2}', '{3}', '{4}', '{5}', '{6}', '{7}', '{8}', '{9}', '{10}', '{11}', '{12}', '{13}', '{14}', '{15}', '{16}', '{17}', '{18}', '{19}'),",
                DateTime.Now.ToString("o", CultureInfo.CurrentCulture), GlobalAttributes.v_Flow1, GlobalAttributes.v_Flow2, GlobalAttributes.v_Flow3, GlobalAttributes.v_Flow4, GlobalAttributes.v_Flow5, GlobalAttributes.v_Rain, GlobalAttributes.v_Ground, GlobalAttributes.v_Level, GlobalAttributes.v_Pressure, GlobalAttributes.v_CPUTemp, GlobalAttributes.v_AirTemp, GlobalAttributes.v_ExpTemp, fGpioServer.GetPinState(1), fGpioServer.GetPinState(2), fGpioServer.GetPinState(3), fGpioServer.GetPinState(4), fGpioServer.GetPinState(5), fGpioServer.GetPinState(6), AppInDebug);
            GlobalAttributes.vSqlDat_Data += SignalSqlString;
            fSqlServer.WriteToSql();
            #endregion SQLlogger
            #region WateringActivation
            #region Morning
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Monday)    && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Monday morning", "The routine on Monday morning for watering of plants was activated.");
                Watering((cMON1 && cMOR1),(cMON2 && cMOR2),(cMON3 && cMOR3),(cMON4 && cMOR4),(cMON5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Tuesday)   && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Tuesday morning", "The routine on Tuesday morning for watering of plants was activated.");
                Watering((cTUE1 && cMOR1), (cTUE2 && cMOR2), (cTUE3 && cMOR3), (cTUE4 && cMOR4), (cTUE5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Wednesday) && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Wednesday morning", "The routine on Wednesday morning for watering of plants was activated.");
                Watering((cWED1 && cMOR1), (cWED2 && cMOR2), (cWED3 && cMOR3), (cWED4 && cMOR4), (cWED5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Thursday)  && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Thursday morning", "The routine on Thursday morning for watering of plants was activated.");
                Watering((cTHU1 && cMOR1), (cTHU2 && cMOR2), (cTHU3 && cMOR3), (cTHU4 && cMOR4), (cTHU5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Friday)    && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Friday morning", "The routine on Friday morning for watering of plants was activated.");
                Watering((cFRI1 && cMOR1), (cFRI2 && cMOR2), (cFRI3 && cMOR3), (cFRI4 && cMOR4), (cFRI5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Saturday)  && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Saturday morning", "The routine on Saturday morning for watering of plants was activated.");
                Watering((cSAT1 && cMOR1), (cSAT2 && cMOR2), (cSAT3 && cMOR3), (cSAT4 && cMOR4), (cSAT5 && cMOR5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Sunday)    && (DateTime.Now.Hour == 7) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Sunday morning", "The routine on Sunday morning for watering of plants was activated.");
                Watering((cSUN1 && cMOR1), (cSUN2 && cMOR2), (cSUN3 && cMOR3), (cSUN4 && cMOR4), (cSUN5 && cMOR5));
            }
            #endregion Morning
            #region Noon
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Monday)    && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Monday noon", "The routine on Monday noon for watering of plants was activated.");
                Watering((cMON1 && cNOO1), (cMON2 && cNOO2), (cMON3 && cNOO3), (cMON4 && cNOO4), (cMON5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Tuesday)   && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Tuesday noon", "The routine on Tuesday noon for watering of plants was activated.");
                Watering((cTUE1 && cNOO1), (cTUE2 && cNOO2), (cTUE3 && cNOO3), (cTUE4 && cNOO4), (cTUE5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Wednesday) && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Wednesday noon", "The routine on Wednesday noon for watering of plants was activated.");
                Watering((cWED1 && cNOO1), (cWED2 && cNOO2), (cWED3 && cNOO3), (cWED4 && cNOO4), (cWED5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Thursday)  && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Thursday noon", "The routine on Thursday noon for watering of plants was activated.");
                Watering((cTHU1 && cNOO1), (cTHU2 && cNOO2), (cTHU3 && cNOO3), (cTHU4 && cNOO4), (cTHU5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Friday)    && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Friday noon", "The routine on Friday noon for watering of plants was activated.");
                Watering((cFRI1 && cNOO1), (cFRI2 && cNOO2), (cFRI3 && cNOO3), (cFRI4 && cNOO4), (cFRI5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Saturday)  && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Saturday noon", "The routine on Saturday noon for watering of plants was activated.");
                Watering((cSAT1 && cNOO1), (cSAT2 && cNOO2), (cSAT3 && cNOO3), (cSAT4 && cNOO4), (cSAT5 && cNOO5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Sunday)    && (DateTime.Now.Hour == 12) && (DateTime.Now.Minute == 0) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Sunday noon", "The routine on Sunday noon for watering of plants was activated.");
                Watering((cSUN1 && cNOO1), (cSUN2 && cNOO2), (cSUN3 && cNOO3), (cSUN4 && cNOO4), (cSUN5 && cNOO5));
            }
            #endregion Noon
            #region Evening
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Monday)    && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Monday evening", "The routine on Monday evening for watering of plants was activated.");
                Watering((cMON1 && cEVE1), (cMON2 && cEVE2), (cMON3 && cEVE3), (cMON4 && cEVE4), (cMON5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Tuesday)   && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Tuesday evening", "The routine on Tuesday evening for watering of plants was activated.");
                Watering((cTUE1 && cEVE1), (cTUE2 && cEVE2), (cTUE3 && cEVE3), (cTUE4 && cEVE4), (cTUE5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Wednesday) && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Wednesday evening", "The routine on Wednesday evening for watering of plants was activated.");
                Watering((cWED1 && cEVE1), (cWED2 && cEVE2), (cWED3 && cEVE3), (cWED4 && cEVE4), (cWED5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Thursday)  && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Thursday evening", "The routine on Thursday evening for watering of plants was activated.");
                Watering((cTHU1 && cEVE1), (cTHU2 && cEVE2), (cTHU3 && cEVE3), (cTHU4 && cEVE4), (cTHU5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Friday)    && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Friday evening", "The routine on Friday evening for watering of plants was activated.");
                Watering((cFRI1 && cEVE1), (cFRI2 && cEVE2), (cFRI3 && cEVE3), (cFRI4 && cEVE4), (cFRI5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Saturday)  && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Saturday evening", "The routine on Saturday evening for watering of plants was activated.");
                Watering((cSAT1 && cEVE1), (cSAT2 && cEVE2), (cSAT3 && cEVE3), (cSAT4 && cEVE4), (cSAT5 && cEVE5));
            }
            if ((DateTime.Now.DayOfWeek == DayOfWeek.Sunday)    && (DateTime.Now.Hour == 18) && (DateTime.Now.Minute == 40) && (DateTime.Now.Second == 1))
            {
                DebugLog("APP", "Status", "Watering Sunday evening", "The routine on Sunday evening for watering of plants was activated.");
                Watering((cSUN1 && cEVE1), (cSUN2 && cEVE2), (cSUN3 && cEVE3), (cSUN4 && cEVE4), (cSUN5 && cEVE5));
            }
            #endregion Evening
            #endregion WateringActivation
        }
        private void LoadSettings()
        {
            this.DebugLog("[APP]", "Information", "Loading configuration.", "The application is loading the configuration from the LoacalSettings storage.");

            ApplicationDataContainer localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            #region GetValues
            byte? sVOL1 = localSettings.Values["VOL1"] as byte?;
            byte? sVOL2 = localSettings.Values["VOL2"] as byte?;
            byte? sVOL3 = localSettings.Values["VOL3"] as byte?;
            byte? sVOL4 = localSettings.Values["VOL4"] as byte?;
            byte? sVOL5 = localSettings.Values["VOL5"] as byte?;

            byte? sRAF1 = localSettings.Values["RAF1"] as byte?;
            byte? sRAF2 = localSettings.Values["RAF2"] as byte?;
            byte? sRAF3 = localSettings.Values["RAF3"] as byte?;
            byte? sRAF4 = localSettings.Values["RAF4"] as byte?;
            byte? sRAF5 = localSettings.Values["RAF5"] as byte?;

            byte? sGAF1 = localSettings.Values["GAF1"] as byte?;
            byte? sGAF2 = localSettings.Values["GAF2"] as byte?;
            byte? sGAF3 = localSettings.Values["GAF3"] as byte?;
            byte? sGAF4 = localSettings.Values["GAF4"] as byte?;
            byte? sGAF5 = localSettings.Values["GAF5"] as byte?;

            bool? sMOR1 = localSettings.Values["MOR1"] as bool?;
            bool? sMOR2 = localSettings.Values["MOR2"] as bool?;
            bool? sMOR3 = localSettings.Values["MOR3"] as bool?;
            bool? sMOR4 = localSettings.Values["MOR4"] as bool?;
            bool? sMOR5 = localSettings.Values["MOR5"] as bool?;

            bool? sNOO1 = localSettings.Values["NOO1"] as bool?;
            bool? sNOO2 = localSettings.Values["NOO2"] as bool?;
            bool? sNOO3 = localSettings.Values["NOO3"] as bool?;
            bool? sNOO4 = localSettings.Values["NOO4"] as bool?;
            bool? sNOO5 = localSettings.Values["NOO5"] as bool?;

            bool? sEVE1 = localSettings.Values["EVE1"] as bool?;
            bool? sEVE2 = localSettings.Values["EVE2"] as bool?;
            bool? sEVE3 = localSettings.Values["EVE3"] as bool?;
            bool? sEVE4 = localSettings.Values["EVE4"] as bool?;
            bool? sEVE5 = localSettings.Values["EVE5"] as bool?;

            bool? sMON1 = localSettings.Values["MON1"] as bool?;
            bool? sMON2 = localSettings.Values["MON2"] as bool?;
            bool? sMON3 = localSettings.Values["MON3"] as bool?;
            bool? sMON4 = localSettings.Values["MON4"] as bool?;
            bool? sMON5 = localSettings.Values["MON5"] as bool?;

            bool? sTUE1 = localSettings.Values["TUE1"] as bool?;
            bool? sTUE2 = localSettings.Values["TUE2"] as bool?;
            bool? sTUE3 = localSettings.Values["TUE3"] as bool?;
            bool? sTUE4 = localSettings.Values["TUE4"] as bool?;
            bool? sTUE5 = localSettings.Values["TUE5"] as bool?;

            bool? sWED1 = localSettings.Values["WED1"] as bool?;
            bool? sWED2 = localSettings.Values["WED2"] as bool?;
            bool? sWED3 = localSettings.Values["WED3"] as bool?;
            bool? sWED4 = localSettings.Values["WED4"] as bool?;
            bool? sWED5 = localSettings.Values["WED5"] as bool?;

            bool? sTHU1 = localSettings.Values["THU1"] as bool?;
            bool? sTHU2 = localSettings.Values["THU2"] as bool?;
            bool? sTHU3 = localSettings.Values["THU3"] as bool?;
            bool? sTHU4 = localSettings.Values["THU4"] as bool?;
            bool? sTHU5 = localSettings.Values["THU5"] as bool?;

            bool? sFRI1 = localSettings.Values["FRI1"] as bool?;
            bool? sFRI2 = localSettings.Values["FRI2"] as bool?;
            bool? sFRI3 = localSettings.Values["FRI3"] as bool?;
            bool? sFRI4 = localSettings.Values["FRI4"] as bool?;
            bool? sFRI5 = localSettings.Values["FRI5"] as bool?;

            bool? sSAT1 = localSettings.Values["SAT1"] as bool?;
            bool? sSAT2 = localSettings.Values["SAT2"] as bool?;
            bool? sSAT3 = localSettings.Values["SAT3"] as bool?;
            bool? sSAT4 = localSettings.Values["SAT4"] as bool?;
            bool? sSAT5 = localSettings.Values["SAT5"] as bool?;

            bool? sSUN1 = localSettings.Values["SUN1"] as bool?;
            bool? sSUN2 = localSettings.Values["SUN2"] as bool?;
            bool? sSUN3 = localSettings.Values["SUN3"] as bool?;
            bool? sSUN4 = localSettings.Values["SUN4"] as bool?;
            bool? sSUN5 = localSettings.Values["SUN5"] as bool?;
            #endregion GetValues
            #region CheckAvailable
            if (sVOL1.HasValue) { cVOL1 = sVOL1.Value; this.DebugLog("[APP]", "Information", "Loaded: cVOL1 = " + cVOL1, "The setting cVOL1 was loaded and set to " + cVOL1); } else { cVOL1 = 0; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sVOL1", "The setting sVOL1 could not be loaded and was replaced by standard value 0."); }
            if (sVOL2.HasValue) { cVOL2 = sVOL2.Value; this.DebugLog("[APP]", "Information", "Loaded: cVOL2 = " + cVOL2, "The setting cVOL1 was loaded and set to " + cVOL2); } else { cVOL2 = 0; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sVOL2", "The setting sVOL2 could not be loaded and was replaced by standard value 0."); }
            if (sVOL3.HasValue) { cVOL3 = sVOL3.Value; this.DebugLog("[APP]", "Information", "Loaded: cVOL3 = " + cVOL3, "The setting cVOL1 was loaded and set to " + cVOL3); } else { cVOL3 = 0; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sVOL3", "The setting sVOL3 could not be loaded and was replaced by standard value 0."); }
            if (sVOL4.HasValue) { cVOL4 = sVOL4.Value; this.DebugLog("[APP]", "Information", "Loaded: cVOL4 = " + cVOL4, "The setting cVOL1 was loaded and set to " + cVOL4); } else { cVOL4 = 0; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sVOL4", "The setting sVOL4 could not be loaded and was replaced by standard value 0."); }
            if (sVOL5.HasValue) { cVOL5 = sVOL5.Value; this.DebugLog("[APP]", "Information", "Loaded: cVOL5 = " + cVOL5, "The setting cVOL1 was loaded and set to " + cVOL5); } else { cVOL5 = 0; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sVOL5", "The setting sVOL5 could not be loaded and was replaced by standard value 0."); }

            if (sRAF1.HasValue) { cRAF1 = sRAF1.Value; this.DebugLog("[APP]", "Information", "Loaded: cRAF1 = " + cRAF1, "The setting cRAF1 was loaded and set to " + cRAF1); } else { cRAF1 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sRAF1", "The setting sRAF1 could not be loaded and was replaced by standard value 0."); }
            if (sRAF2.HasValue) { cRAF2 = sRAF2.Value; this.DebugLog("[APP]", "Information", "Loaded: cRAF2 = " + cRAF2, "The setting cRAF2 was loaded and set to " + cRAF2); } else { cRAF2 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sRAF2", "The setting sRAF2 could not be loaded and was replaced by standard value 0."); }
            if (sRAF3.HasValue) { cRAF3 = sRAF3.Value; this.DebugLog("[APP]", "Information", "Loaded: cRAF3 = " + cRAF3, "The setting cRAF3 was loaded and set to " + cRAF3); } else { cRAF3 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sRAF3", "The setting sRAF3 could not be loaded and was replaced by standard value 0."); }
            if (sRAF4.HasValue) { cRAF4 = sRAF4.Value; this.DebugLog("[APP]", "Information", "Loaded: cRAF4 = " + cRAF4, "The setting cRAF4 was loaded and set to " + cRAF4); } else { cRAF4 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sRAF4", "The setting sRAF4 could not be loaded and was replaced by standard value 0."); }
            if (sRAF5.HasValue) { cRAF5 = sRAF5.Value; this.DebugLog("[APP]", "Information", "Loaded: cRAF5 = " + cRAF5, "The setting cRAF5 was loaded and set to " + cRAF5); } else { cRAF5 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sRAF5", "The setting sRAF5 could not be loaded and was replaced by standard value 0."); }

            if (sGAF1.HasValue) { cGAF1 = sGAF1.Value; this.DebugLog("[APP]", "Information", "Loaded: cGAF1 = " + cGAF1, "The setting cGAF1 was loaded and set to " + cGAF1); } else { cGAF1 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sGAF1", "The setting sGAF1 could not be loaded and was replaced by standard value 0."); }
            if (sGAF2.HasValue) { cGAF2 = sGAF2.Value; this.DebugLog("[APP]", "Information", "Loaded: cGAF2 = " + cGAF2, "The setting cGAF2 was loaded and set to " + cGAF2); } else { cGAF2 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sGAF2", "The setting sGAF2 could not be loaded and was replaced by standard value 0."); }
            if (sGAF3.HasValue) { cGAF3 = sGAF3.Value; this.DebugLog("[APP]", "Information", "Loaded: cGAF3 = " + cGAF3, "The setting cGAF3 was loaded and set to " + cGAF3); } else { cGAF3 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sGAF3", "The setting sGAF3 could not be loaded and was replaced by standard value 0."); }
            if (sGAF4.HasValue) { cGAF4 = sGAF4.Value; this.DebugLog("[APP]", "Information", "Loaded: cGAF4 = " + cGAF4, "The setting cGAF4 was loaded and set to " + cGAF4); } else { cGAF4 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sGAF4", "The setting sGAF4 could not be loaded and was replaced by standard value 0."); }
            if (sGAF5.HasValue) { cGAF5 = sGAF5.Value; this.DebugLog("[APP]", "Information", "Loaded: cGAF5 = " + cGAF5, "The setting cGAF5 was loaded and set to " + cGAF5); } else { cGAF5 = 100; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sGAF5", "The setting sGAF5 could not be loaded and was replaced by standard value 0."); }

            if (sMOR1.HasValue) { cMOR1 = sMOR1.Value; this.DebugLog("[APP]", "Information", "Loaded: cMOR1 = " + cMOR1, "The setting cMOR1 was loaded and set to " + cMOR1); } else { cMOR1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMOR1", "The setting sMOR1 could not be loaded and was replaced by standard value false."); }
            if (sMOR2.HasValue) { cMOR2 = sMOR2.Value; this.DebugLog("[APP]", "Information", "Loaded: cMOR2 = " + cMOR2, "The setting cMOR2 was loaded and set to " + cMOR2); } else { cMOR2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMOR2", "The setting sMOR2 could not be loaded and was replaced by standard value false."); }
            if (sMOR3.HasValue) { cMOR3 = sMOR3.Value; this.DebugLog("[APP]", "Information", "Loaded: cMOR3 = " + cMOR3, "The setting cMOR3 was loaded and set to " + cMOR3); } else { cMOR3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMOR3", "The setting sMOR3 could not be loaded and was replaced by standard value false."); }
            if (sMOR4.HasValue) { cMOR4 = sMOR4.Value; this.DebugLog("[APP]", "Information", "Loaded: cMOR4 = " + cMOR4, "The setting cMOR4 was loaded and set to " + cMOR4); } else { cMOR4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMOR4", "The setting sMOR4 could not be loaded and was replaced by standard value false."); }
            if (sMOR5.HasValue) { cMOR5 = sMOR5.Value; this.DebugLog("[APP]", "Information", "Loaded: cMOR5 = " + cMOR5, "The setting cMOR5 was loaded and set to " + cMOR5); } else { cMOR5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMOR5", "The setting sMOR5 could not be loaded and was replaced by standard value false."); }

            if (sNOO1.HasValue) { cNOO1 = sNOO1.Value; this.DebugLog("[APP]", "Information", "Loaded: cNOO1 = " + cNOO1, "The setting cNOO1 was loaded and set to " + cNOO1); } else { cNOO1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sNOO1", "The setting sNOO1 could not be loaded and was replaced by standard value false."); }
            if (sNOO2.HasValue) { cNOO2 = sNOO2.Value; this.DebugLog("[APP]", "Information", "Loaded: cNOO2 = " + cNOO2, "The setting cNOO2 was loaded and set to " + cNOO2); } else { cNOO2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sNOO2", "The setting sNOO2 could not be loaded and was replaced by standard value false."); }
            if (sNOO3.HasValue) { cNOO3 = sNOO3.Value; this.DebugLog("[APP]", "Information", "Loaded: cNOO3 = " + cNOO3, "The setting cNOO3 was loaded and set to " + cNOO3); } else { cNOO3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sNOO3", "The setting sNOO3 could not be loaded and was replaced by standard value false."); }
            if (sNOO4.HasValue) { cNOO4 = sNOO4.Value; this.DebugLog("[APP]", "Information", "Loaded: cNOO4 = " + cNOO4, "The setting cNOO4 was loaded and set to " + cNOO4); } else { cNOO4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sNOO4", "The setting sNOO4 could not be loaded and was replaced by standard value false."); }
            if (sNOO5.HasValue) { cNOO5 = sNOO5.Value; this.DebugLog("[APP]", "Information", "Loaded: cNOO5 = " + cNOO5, "The setting cNOO5 was loaded and set to " + cNOO5); } else { cNOO5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sNOO5", "The setting sNOO5 could not be loaded and was replaced by standard value false."); }

            if (sEVE1.HasValue) { cEVE1 = sEVE1.Value; this.DebugLog("[APP]", "Information", "Loaded: cEVE1 = " + cEVE1, "The setting cEVE1 was loaded and set to " + cEVE1); } else { cEVE1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sEVE1", "The setting sEVE1 could not be loaded and was replaced by standard value false."); }
            if (sEVE2.HasValue) { cEVE2 = sEVE2.Value; this.DebugLog("[APP]", "Information", "Loaded: cEVE2 = " + cEVE2, "The setting cEVE2 was loaded and set to " + cEVE2); } else { cEVE2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sEVE2", "The setting sEVE2 could not be loaded and was replaced by standard value false."); }
            if (sEVE3.HasValue) { cEVE3 = sEVE3.Value; this.DebugLog("[APP]", "Information", "Loaded: cEVE3 = " + cEVE3, "The setting cEVE3 was loaded and set to " + cEVE3); } else { cEVE3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sEVE3", "The setting sEVE3 could not be loaded and was replaced by standard value false."); }
            if (sEVE4.HasValue) { cEVE4 = sEVE4.Value; this.DebugLog("[APP]", "Information", "Loaded: cEVE4 = " + cEVE4, "The setting cEVE4 was loaded and set to " + cEVE4); } else { cEVE4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sEVE4", "The setting sEVE4 could not be loaded and was replaced by standard value false."); }
            if (sEVE5.HasValue) { cEVE5 = sEVE5.Value; this.DebugLog("[APP]", "Information", "Loaded: cEVE5 = " + cEVE5, "The setting cEVE5 was loaded and set to " + cEVE5); } else { cEVE5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sEVE5", "The setting sEVE5 could not be loaded and was replaced by standard value false."); }

            if (sMON1.HasValue) { cMON1 = sMON1.Value; this.DebugLog("[APP]", "Information", "Loaded: cMON1 = " + cMON1, "The setting cMON1 was loaded and set to " + cMON1); } else { cMON1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMON1", "The setting sMON1 could not be loaded and was replaced by standard value false."); }
            if (sMON2.HasValue) { cMON2 = sMON2.Value; this.DebugLog("[APP]", "Information", "Loaded: cMON2 = " + cMON2, "The setting cMON2 was loaded and set to " + cMON2); } else { cMON2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMON2", "The setting sMON2 could not be loaded and was replaced by standard value false."); }
            if (sMON3.HasValue) { cMON3 = sMON3.Value; this.DebugLog("[APP]", "Information", "Loaded: cMON3 = " + cMON3, "The setting cMON3 was loaded and set to " + cMON3); } else { cMON3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMON3", "The setting sMON3 could not be loaded and was replaced by standard value false."); }
            if (sMON4.HasValue) { cMON4 = sMON4.Value; this.DebugLog("[APP]", "Information", "Loaded: cMON4 = " + cMON4, "The setting cMON4 was loaded and set to " + cMON4); } else { cMON4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMON4", "The setting sMON4 could not be loaded and was replaced by standard value false."); }
            if (sMON5.HasValue) { cMON5 = sMON5.Value; this.DebugLog("[APP]", "Information", "Loaded: cMON5 = " + cMON5, "The setting cMON5 was loaded and set to " + cMON5); } else { cMON5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sMON5", "The setting sMON5 could not be loaded and was replaced by standard value false."); }

            if (sTUE1.HasValue) { cTUE1 = sTUE1.Value; this.DebugLog("[APP]", "Information", "Loaded: cTUE1 = " + cTUE1, "The setting cTUE1 was loaded and set to " + cTUE1); } else { cTUE1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTUE1", "The setting sTUE1 could not be loaded and was replaced by standard value false."); }
            if (sTUE2.HasValue) { cTUE2 = sTUE2.Value; this.DebugLog("[APP]", "Information", "Loaded: cTUE2 = " + cTUE2, "The setting cTUE2 was loaded and set to " + cTUE2); } else { cTUE2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTUE2", "The setting sTUE2 could not be loaded and was replaced by standard value false."); }
            if (sTUE3.HasValue) { cTUE3 = sTUE3.Value; this.DebugLog("[APP]", "Information", "Loaded: cTUE3 = " + cTUE3, "The setting cTUE3 was loaded and set to " + cTUE3); } else { cTUE3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTUE3", "The setting sTUE3 could not be loaded and was replaced by standard value false."); }
            if (sTUE4.HasValue) { cTUE4 = sTUE4.Value; this.DebugLog("[APP]", "Information", "Loaded: cTUE4 = " + cTUE4, "The setting cTUE4 was loaded and set to " + cTUE4); } else { cTUE4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTUE4", "The setting sTUE4 could not be loaded and was replaced by standard value false."); }
            if (sTUE5.HasValue) { cTUE5 = sTUE5.Value; this.DebugLog("[APP]", "Information", "Loaded: cTUE5 = " + cTUE5, "The setting cTUE5 was loaded and set to " + cTUE5); } else { cTUE5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTUE5", "The setting sTUE5 could not be loaded and was replaced by standard value false."); }

            if (sWED1.HasValue) { cWED1 = sWED1.Value; this.DebugLog("[APP]", "Information", "Loaded: cWED1 = " + cWED1, "The setting cWED1 was loaded and set to " + cWED1); } else { cWED1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sWED1", "The setting sWED1 could not be loaded and was replaced by standard value false."); }
            if (sWED2.HasValue) { cWED2 = sWED2.Value; this.DebugLog("[APP]", "Information", "Loaded: cWED2 = " + cWED2, "The setting cWED2 was loaded and set to " + cWED2); } else { cWED2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sWED2", "The setting sWED2 could not be loaded and was replaced by standard value false."); }
            if (sWED3.HasValue) { cWED3 = sWED3.Value; this.DebugLog("[APP]", "Information", "Loaded: cWED3 = " + cWED3, "The setting cWED3 was loaded and set to " + cWED3); } else { cWED3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sWED3", "The setting sWED3 could not be loaded and was replaced by standard value false."); }
            if (sWED4.HasValue) { cWED4 = sWED4.Value; this.DebugLog("[APP]", "Information", "Loaded: cWED4 = " + cWED4, "The setting cWED4 was loaded and set to " + cWED4); } else { cWED4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sWED4", "The setting sWED4 could not be loaded and was replaced by standard value false."); }
            if (sWED5.HasValue) { cWED5 = sWED5.Value; this.DebugLog("[APP]", "Information", "Loaded: cWED5 = " + cWED5, "The setting cWED5 was loaded and set to " + cWED5); } else { cWED5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sWED5", "The setting sWED5 could not be loaded and was replaced by standard value false."); }

            if (sTHU1.HasValue) { cTHU1 = sTHU1.Value; this.DebugLog("[APP]", "Information", "Loaded: cTHU1 = " + cTHU1, "The setting cTHU1 was loaded and set to " + cTHU1); } else { cTHU1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTHU1", "The setting sTHU1 could not be loaded and was replaced by standard value false."); }
            if (sTHU2.HasValue) { cTHU2 = sTHU2.Value; this.DebugLog("[APP]", "Information", "Loaded: cTHU2 = " + cTHU2, "The setting cTHU2 was loaded and set to " + cTHU2); } else { cTHU2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTHU2", "The setting sTHU2 could not be loaded and was replaced by standard value false."); }
            if (sTHU3.HasValue) { cTHU3 = sTHU3.Value; this.DebugLog("[APP]", "Information", "Loaded: cTHU3 = " + cTHU3, "The setting cTHU3 was loaded and set to " + cTHU3); } else { cTHU3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTHU3", "The setting sTHU3 could not be loaded and was replaced by standard value false."); }
            if (sTHU4.HasValue) { cTHU4 = sTHU4.Value; this.DebugLog("[APP]", "Information", "Loaded: cTHU4 = " + cTHU4, "The setting cTHU4 was loaded and set to " + cTHU4); } else { cTHU4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTHU4", "The setting sTHU4 could not be loaded and was replaced by standard value false."); }
            if (sTHU5.HasValue) { cTHU5 = sTHU5.Value; this.DebugLog("[APP]", "Information", "Loaded: cTHU5 = " + cTHU5, "The setting cTHU5 was loaded and set to " + cTHU5); } else { cTHU5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sTHU5", "The setting sTHU5 could not be loaded and was replaced by standard value false."); }

            if (sFRI1.HasValue) { cFRI1 = sFRI1.Value; this.DebugLog("[APP]", "Information", "Loaded: cFRI1 = " + cFRI1, "The setting cFRI1 was loaded and set to " + cFRI1); } else { cFRI1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sFRI1", "The setting sFRI1 could not be loaded and was replaced by standard value false."); }
            if (sFRI2.HasValue) { cFRI2 = sFRI2.Value; this.DebugLog("[APP]", "Information", "Loaded: cFRI2 = " + cFRI2, "The setting cFRI2 was loaded and set to " + cFRI2); } else { cFRI2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sFRI2", "The setting sFRI2 could not be loaded and was replaced by standard value false."); }
            if (sFRI3.HasValue) { cFRI3 = sFRI3.Value; this.DebugLog("[APP]", "Information", "Loaded: cFRI3 = " + cFRI3, "The setting cFRI3 was loaded and set to " + cFRI3); } else { cFRI3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sFRI3", "The setting sFRI3 could not be loaded and was replaced by standard value false."); }
            if (sFRI4.HasValue) { cFRI4 = sFRI4.Value; this.DebugLog("[APP]", "Information", "Loaded: cFRI4 = " + cFRI4, "The setting cFRI4 was loaded and set to " + cFRI4); } else { cFRI4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sFRI4", "The setting sFRI4 could not be loaded and was replaced by standard value false."); }
            if (sFRI5.HasValue) { cFRI5 = sFRI5.Value; this.DebugLog("[APP]", "Information", "Loaded: cFRI5 = " + cFRI5, "The setting cFRI5 was loaded and set to " + cFRI5); } else { cFRI5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sFRI5", "The setting sFRI5 could not be loaded and was replaced by standard value false."); }

            if (sSAT1.HasValue) { cSAT1 = sSAT1.Value; this.DebugLog("[APP]", "Information", "Loaded: cSAT1 = " + cSAT1, "The setting cSAT1 was loaded and set to " + cSAT1); } else { cSAT1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSAT1", "The setting sSAT1 could not be loaded and was replaced by standard value false."); }
            if (sSAT2.HasValue) { cSAT2 = sSAT2.Value; this.DebugLog("[APP]", "Information", "Loaded: cSAT2 = " + cSAT2, "The setting cSAT2 was loaded and set to " + cSAT2); } else { cSAT2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSAT2", "The setting sSAT2 could not be loaded and was replaced by standard value false."); }
            if (sSAT3.HasValue) { cSAT3 = sSAT3.Value; this.DebugLog("[APP]", "Information", "Loaded: cSAT3 = " + cSAT3, "The setting cSAT3 was loaded and set to " + cSAT3); } else { cSAT3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSAT3", "The setting sSAT3 could not be loaded and was replaced by standard value false."); }
            if (sSAT4.HasValue) { cSAT4 = sSAT4.Value; this.DebugLog("[APP]", "Information", "Loaded: cSAT4 = " + cSAT4, "The setting cSAT4 was loaded and set to " + cSAT4); } else { cSAT4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSAT4", "The setting sSAT4 could not be loaded and was replaced by standard value false."); }
            if (sSAT5.HasValue) { cSAT5 = sSAT5.Value; this.DebugLog("[APP]", "Information", "Loaded: cSAT5 = " + cSAT5, "The setting cSAT5 was loaded and set to " + cSAT5); } else { cSAT5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSAT5", "The setting sSAT5 could not be loaded and was replaced by standard value false."); }

            if (sSUN1.HasValue) { cSUN1 = sSUN1.Value; this.DebugLog("[APP]", "Information", "Loaded: cSUN1 = " + cSUN1, "The setting cSUN1 was loaded and set to " + cSUN1); } else { cSUN1 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSUN1", "The setting sSUN1 could not be loaded and was replaced by standard value false."); }
            if (sSUN2.HasValue) { cSUN2 = sSUN2.Value; this.DebugLog("[APP]", "Information", "Loaded: cSUN2 = " + cSUN2, "The setting cSUN2 was loaded and set to " + cSUN2); } else { cSUN2 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSUN2", "The setting sSUN2 could not be loaded and was replaced by standard value false."); }
            if (sSUN3.HasValue) { cSUN3 = sSUN3.Value; this.DebugLog("[APP]", "Information", "Loaded: cSUN3 = " + cSUN3, "The setting cSUN3 was loaded and set to " + cSUN3); } else { cSUN3 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSUN3", "The setting sSUN3 could not be loaded and was replaced by standard value false."); }
            if (sSUN4.HasValue) { cSUN4 = sSUN4.Value; this.DebugLog("[APP]", "Information", "Loaded: cSUN4 = " + cSUN4, "The setting cSUN4 was loaded and set to " + cSUN4); } else { cSUN4 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSUN4", "The setting sSUN4 could not be loaded and was replaced by standard value false."); }
            if (sSUN5.HasValue) { cSUN5 = sSUN5.Value; this.DebugLog("[APP]", "Information", "Loaded: cSUN5 = " + cSUN5, "The setting cSUN5 was loaded and set to " + cSUN5); } else { cSUN5 = false; this.DebugLog("[APP]", "Warning", "Setting could not be loaded sSUN5", "The setting sSUN5 could not be loaded and was replaced by standard value false."); }

            #endregion CheckAvailable

        }
        private void WriteSettingByte(string vSetting, byte vValue)
        {
            string vStr = String.Format("Writing parameter {0} to {1}", vSetting, vValue);
            this.DebugLog("[APP]", "Information", vStr, "A parameter was written into the application configuration storage.");
            ApplicationDataContainer localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values[vSetting] = vValue;
        }
        private void WriteSettingBool(string vSetting, bool vValue)
        {
            string vStr = String.Format("Writing parameter {0} to {1}", vSetting, vValue);
            this.DebugLog("[APP]", "Information", vStr, "A parameter was written into the application configuration storage.");
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[vSetting] = vValue;
        }
        private void Watering(bool Out1_active, bool Out2_active, bool Out3_active, bool Out4_active, bool Out5_active)
        {
            byte vMaxWateringTime = 100;
            byte wt1 = 0;
            byte wt2 = 0;
            byte wt3 = 0;
            byte wt4 = 0;
            byte wt5 = 0;

            byte tmp_GAF1 = cGAF1;
            byte tmp_GAF2 = cGAF2;
            byte tmp_GAF3 = cGAF3;
            byte tmp_GAF4 = cGAF4;
            byte tmp_GAF5 = cGAF5;

            byte tmp_RAF1 = cRAF1;
            byte tmp_RAF2 = cRAF2;
            byte tmp_RAF3 = cRAF3;
            byte tmp_RAF4 = cRAF4;
            byte tmp_RAF5 = cRAF5;

            if (GlobalAttributes.v_Rain > 200)     // Rain
            {
                tmp_RAF1 = cRAF1;
                tmp_RAF2 = cRAF2;
                tmp_RAF3 = cRAF3;
                tmp_RAF4 = cRAF4;
                tmp_RAF5 = cRAF5;
            }
            else
            {
                tmp_RAF1 = 100;
                tmp_RAF2 = 100;
                tmp_RAF3 = 100;
                tmp_RAF4 = 100;
                tmp_RAF5 = 100;
            }

            if (GlobalAttributes.v_Ground > 200)   // Ground damp
            {
                tmp_GAF1 = cGAF1;
                tmp_GAF2 = cGAF2;
                tmp_GAF3 = cGAF3;
                tmp_GAF4 = cGAF4;
                tmp_GAF5 = cGAF5;
            }  
            else
            {
                tmp_GAF1 = 100;
                tmp_GAF2 = 100;
                tmp_GAF3 = 100;
                tmp_GAF4 = 100;
                tmp_GAF5 = 100;
            }

            int tmp_Vol1 = cVOL1 * (tmp_RAF1 / 100) * (tmp_GAF1 / 100);
            int tmp_Vol2 = cVOL2 * (tmp_RAF2 / 100) * (tmp_GAF2 / 100);
            int tmp_Vol3 = cVOL3 * (tmp_RAF3 / 100) * (tmp_GAF3 / 100);
            int tmp_Vol4 = cVOL4 * (tmp_RAF4 / 100) * (tmp_GAF4 / 100);
            int tmp_Vol5 = cVOL5 * (tmp_RAF5 / 100) * (tmp_GAF5 / 100);

            if (tmp_Vol1 > 255) { tmp_Vol1 = 255; }
            if (tmp_Vol2 > 255) { tmp_Vol2 = 255; }
            if (tmp_Vol3 > 255) { tmp_Vol3 = 255; }
            if (tmp_Vol4 > 255) { tmp_Vol4 = 255; }
            if (tmp_Vol5 > 255) { tmp_Vol5 = 255; }


            if (Out1_active)
            {
                this.DebugLog("[WAT]", "Status", "Watering Plant 1", "The routine activated watering of plant #1.");
                fGpioServer.SetPinState(2, true);                                                       // Open Valve #1
                var t_wait = Task.Run(async delegate { await Task.Delay(2000); });
                t_wait.Wait();     // wait 2s
                var t_Water1 = Task.Run(async delegate
                {
                    fGpioServer.SetPinState(1, true);                                                   // Start Pump
                    while (fTwiServer.TWI_ATmega_ReadFlow(0) < tmp_Vol1)
                    {
                        await Task.Delay(1000);
                        wt1++;
                        if (wt1 >= vMaxWateringTime)
                        {
                            this.DebugLog("[WAT]", "Warning", "Watering #1 reached max time", "The watering procedure on line #1 reached the maximum time for watering and was aborted.");
                            break;
                        }
                    }
                    fGpioServer.SetPinState(1, false);                                                  // Stop Pump
                });
                t_Water1.Wait();                                                                        // Check flow every second and wait for full volume watering
                this.DebugLog("[WAT]", "Information", "Watering Plant 2 finsihed", "The watering procedure of Plant 2 ended.");
                t_wait = Task.Run(async delegate { await Task.Delay(5000); });
                t_wait.Wait();                                                                          // wait 5s to depressurize
                fGpioServer.SetPinState(2, false);                                                      // Close Valve #1
            }
            if (Out2_active)
            {
                DebugLog("[WAT]", "Status", "Watering Plant 2", "The routine activated watering of plant #2.");
                fGpioServer.SetPinState(3, true);                                                       // Open Valve #2
                var t_wait = Task.Run(async delegate { await Task.Delay(2000); });
                t_wait.Wait();     // wait 2s
                var t_Water2 = Task.Run(async delegate
                {
                    fGpioServer.SetPinState(1, true);                                                   // Start Pump
                    while (fTwiServer.TWI_ATmega_ReadFlow(1) < tmp_Vol2)
                    {
                        await Task.Delay(1000);
                        wt2++;
                        if (wt2 >= vMaxWateringTime)
                        {
                            this.DebugLog("[WAT]", "Warning", "Watering #2 reached max time", "The watering procedure on line #2 reached the maximum time for watering and was aborted.");
                            break;
                        }
                    }
                    fGpioServer.SetPinState(1, false);                                                  // Stop Pump
                });
                t_Water2.Wait();                                             // Check flow every second and wait for full volume watering
                this.DebugLog("[WAT]", "Information", "Watering Plant 2 finsihed", "The watering procedure of Plant 2 ended.");
                t_wait = Task.Run(async delegate
                {
                    await Task.Delay(5000);
                });
                t_wait.Wait();     // wait 5s to depressurize
                fGpioServer.SetPinState(3, false);                                                      // Close Valve #2
            }
            if (Out3_active)
            {
                DebugLog("APP", "Status", "Watering Plant 3", "The routine activated watering of plant #3.");
                fGpioServer.SetPinState(4, true);                                                       // Open Valve #3
                var t_wait = Task.Run(async delegate { await Task.Delay(2000); });
                t_wait.Wait();     // wait 2s
                var t_Water3 = Task.Run(async delegate
                {
                    fGpioServer.SetPinState(1, true);                                                   // Start Pump
                    while (fTwiServer.TWI_ATmega_ReadFlow(2) < tmp_Vol3)
                    {
                        await Task.Delay(1000);
                        wt3++;
                        if (wt3 >= vMaxWateringTime)
                        {
                            this.DebugLog("[WAT]", "Warning", "Watering #3 reached max time", "The watering procedure on line #3 reached the maximum time for watering and was aborted.");
                            break;
                        }
                    }
                    fGpioServer.SetPinState(1, false);                                                  // Stop Pump
                });
                t_Water3.Wait();                                             // Check flow every second and wait for full volume watering
                this.DebugLog("[WAT]", "Information", "Watering Plant 3 finsihed", "The watering procedure of Plant 3 ended.");
                t_wait = Task.Run(async delegate
                {
                    await Task.Delay(5000);
                });
                t_wait.Wait();     // wait 5s to depressurize
                fGpioServer.SetPinState(4, false);                                                      // Close Valve #3
            }
            if (Out4_active)
            {
                DebugLog("APP", "Status", "Watering Plant 4", "The routine activated watering of plant #4.");
                fGpioServer.SetPinState(5, true);                                                       // Open Valve #4
                var t_wait = Task.Run(async delegate { await Task.Delay(2000); });
                t_wait.Wait();     // wait 2s
                var t_Water4 = Task.Run(async delegate
                {
                    fGpioServer.SetPinState(1, true);                                                   // Start Pump
                    while (fTwiServer.TWI_ATmega_ReadFlow(3) < tmp_Vol4)
                    {
                        await Task.Delay(1000);
                        wt4++;
                        if (wt4 >= vMaxWateringTime)
                        {
                            this.DebugLog("[WAT]", "Warning", "Watering #4 reached max time", "The watering procedure on line #4 reached the maximum time for watering and was aborted.");
                            break;
                        }
                    }
                    fGpioServer.SetPinState(1, false);                                                  // Stop Pump
                });
                t_Water4.Wait();                                             // Check flow every second and wait for full volume watering
                this.DebugLog("[WAT]", "Information", "Watering Plant 4 finsihed", "The watering procedure of Plant 4 ended.");
                t_wait = Task.Run(async delegate
                {
                    await Task.Delay(5000);
                });
                t_wait.Wait();     // wait 5s to depressurize
                fGpioServer.SetPinState(5, false);                                                      // Close Valve #4
            }
            if (Out5_active)
            {
                DebugLog("APP", "Status", "Watering Plant 5", "The routine activated watering of plant #5.");
                fGpioServer.SetPinState(6, true);                                                       // Open Valve #5
                var t_wait = Task.Run(async delegate { await Task.Delay(2000); });
                t_wait.Wait();     // wait 2s
                var t_Water5 = Task.Run(async delegate
                {
                    fGpioServer.SetPinState(1, true);                                                   // Start Pump
                    while (fTwiServer.TWI_ATmega_ReadFlow(4) < tmp_Vol5)
                    {
                        await Task.Delay(1000);
                        wt5++;
                        if (wt5 >= vMaxWateringTime)
                        {
                            this.DebugLog("[WAT]", "Warning", "Watering #5 reached max time", "The watering procedure on line #5 reached the maximum time for watering and was aborted.");
                            break;
                        }
                    }
                    fGpioServer.SetPinState(1, false);                                                  // Stop Pump
                });
                t_Water5.Wait();                                             // Check flow every second and wait for full volume watering
                this.DebugLog("[WAT]", "Information", "Watering Plant 5 finsihed", "The watering procedure of Plant 5 ended.");
                t_wait = Task.Run(async delegate
                {
                    await Task.Delay(5000);
                });
                t_wait.Wait();     // wait 5s to depressurize
                fGpioServer.SetPinState(6, false);                                                      // Close Valve #5
            }

            fTwiServer.TWI_ATmega_ResetCounter();
            this.DebugLog("[APP]", "Information", "Counters reset", "The flow counters were reset after watering.");
        }
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[APP] The application has started");

            // Create new file at system startup
            GlobalAttributes.LocalLogPath = DateTime.Now.Year.ToString("yyyy_MM_dd__HH_mm_ss") + " - Log.txt";
            using (StreamWriter LogFile = File.CreateText(GlobalAttributes.LocalLogPath))
            {
                Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff - ") + "[APP] Creating Local Log file (" + GlobalAttributes.LocalLogPath + ")");
                LogFile.WriteLine("WateringOS_0_4");
                LogFile.WriteLine("(C)2019 by Michael Kollmeyer");
                LogFile.WriteLine("");
                LogFile.WriteLine("TimeStamp;Instance;Type;Name;Detail");
            }

            fDef = taskInstance.GetDeferral();          // get deferral to keep running

            fSqlServer = new SqlServer();
            fSqlServer.vParent = this;
            fSqlServer.InitSql();

            this.DebugLog("[APP]", "Information", "Start application", "The application has started after a boot and the IBackgroundTaskInstance has been entered.");

            LoadSettings();

            fTwiServer = new TwiServer();
            fTwiServer.vParent = this;
            fTwiServer.InitTWIAsync();

            fGpioServer = new GpioServer();
            fGpioServer.vParent = this;
            fGpioServer.InitGPIO();

            this.DebugLog("[NET]", "Information", "Start TCP Server", "The TCP Server initialization for receiving API commands to control the system has been started");
            fTcpServer = new TcpServer();
            fTcpServer.RequestReceived = (request) =>
            {
                byte vTempSettingByte;
                bool vTempSettingBool;
                var vQueryCmd = "";
                var vQueryPar = "";
                var requestLines = request.ToString().Split(' ');
                var url = requestLines.Length > 1 ? requestLines[1] : string.Empty;
                var uri = new Uri("http://localhost" + url);
                if (uri.Query != "")
                {
                    this.DebugLog("[NET]", "Information", "uri-query: " + uri.Query, "Following request has been received via the TCP server: " + uri.Query);
                    if (uri.Query.Length >= 4) { vQueryCmd = uri.Query.Substring(0, 4); } else { vQueryCmd = "empty command"; }
                    if (uri.Query.Length >= 8) { vQueryPar = uri.Query.Substring(5, 3); } else { vQueryPar = "empty parameter"; }
                    switch (vQueryCmd)
                    {
                        case "?120":
                            this.DebugLog("[NET]", "Information", "Command reset flow received, ATmega counter reset", "Following request has been received via the TCP server: " + uri.Query);
                            fTwiServer.TWI_ATmega_ResetCounter();
                            return "Reset flow counters";
                        //------ cmd 13n = DOn -> ON  ------------------------------------
                        case "?130":
                            this.DebugLog("[NET]", "Information", "Command all on received, ALL VALVES -> OPEN", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(2, true);
                            fGpioServer.SetPinState(3, true);
                            fGpioServer.SetPinState(4, true);
                            fGpioServer.SetPinState(5, true);
                            fGpioServer.SetPinState(6, true);
                            return "All valves opened";
                        case "?131":
                            this.DebugLog("[NET]", "Information", "Command Start Pump received, DO_1 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(1, true);
                            return "Pump Started";
                        case "?132":
                            this.DebugLog("[NET]", "Information", "Command OpenValve1 received, DO_2 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(2, true);
                            return "Valve #1 opened";
                        case "?133":
                            this.DebugLog("[NET]", "Information", "Command OpenValve2 received, DO_3 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(3, true);
                            return "Valve #2 opened";
                        case "?134":
                            this.DebugLog("[NET]", "Information", "Command OpenValve3 received, DO_4 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(4, true);
                            return "Valve #3 opened";
                        case "?135":
                            this.DebugLog("[NET]", "Information", "Command OpenValve4 received, DO_5 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(5, true);
                            return "Valve #4 opened";
                        case "?136":
                            this.DebugLog("[NET]", "Information", "Command OpenValve5 received, DO_6 -> ON", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(6, true);
                            return "Valve #5 opened";
                        //------ cmd 14n = DOn -> OFF  ------------------------------------
                        case "?140":
                            this.DebugLog("[NET]", "Information", "Command AllOff received, ALL -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(1, false);
                            fGpioServer.SetPinState(2, false);
                            fGpioServer.SetPinState(3, false);
                            fGpioServer.SetPinState(4, false);
                            fGpioServer.SetPinState(5, false);
                            fGpioServer.SetPinState(6, false);
                            return "All Outputs Low";
                        case "?141":
                            this.DebugLog("[NET]", "Information", "Command StopPump received, DO_1 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(1, false);
                            return "Pump stopped";
                        case "?142":
                            this.DebugLog("[NET]", "Information", "Command CloseValve1 received, DO_2 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(2, false);
                            return "Valve #1 closed";
                        case "?143":
                            this.DebugLog("[NET]", "Information", "Command CloseValve2 received, DO_3 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(3, false);
                            return "Valve #2 closed";
                        case "?144":
                            this.DebugLog("[NET]", "Information", "Command CloseValve3 received, DO_4 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(4, false);
                            return "Valve #3 closed";
                        case "?145":
                            this.DebugLog("[NET]", "Information", "Command CloseValve4 received, DO_5 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(5, false);
                            return "Valve #4 closed";
                        case "?146":
                            this.DebugLog("[NET]", "Information", "Command CloseValve5 received, DO_6 -> OFF", "Following request has been received via the TCP server: " + uri.Query);
                            fGpioServer.SetPinState(6, false);
                            return "Valve #5 closed";
                        case "?200":
                            this.DebugLog("[NET]", "Information", "Pump: " + Convert.ToString(fGpioServer.GetPinState(1)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Valve #1: " + Convert.ToString(fGpioServer.GetPinState(2)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Valve #2: " + Convert.ToString(fGpioServer.GetPinState(3)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Valve #3: " + Convert.ToString(fGpioServer.GetPinState(4)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Valve #4: " + Convert.ToString(fGpioServer.GetPinState(5)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Valve #5: " + Convert.ToString(fGpioServer.GetPinState(6)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Power 5V: " + Convert.ToString(fGpioServer.GetPinState(7)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Power 12V: " + Convert.ToString(fGpioServer.GetPinState(8)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Power 24V: " + Convert.ToString(fGpioServer.GetPinState(9)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "CPU Temp: " + Convert.ToString(fTwiServer.TWI_CPUTemp_Read()), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Flow #1: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadFlow(0)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Flow #2: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadFlow(1)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Flow #3: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadFlow(2)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Flow #4: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadFlow(3)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Flow #5: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadFlow(4)), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Pressure: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadPressure()), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Rain: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadRain()), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Ground: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadGround()), "Debug command for sensor data received.");
                            this.DebugLog("[NET]", "Information", "Level: " + Convert.ToString(fTwiServer.TWI_ATmega_ReadLevel()), "Debug command for sensor data received.");

                            return "Command 200 (Debug sensor data) succesfull";
                        case "?501":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("VOL1", vTempSettingByte);
                            cVOL1 = vTempSettingByte;
                            return "Parameter was written";
                        case "?601":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("VOL2", vTempSettingByte);
                            cVOL2 = vTempSettingByte;
                            return "Parameter was written";
                        case "?701":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("VOL3", vTempSettingByte);
                            cVOL3 = vTempSettingByte;
                            return "Parameter was written";
                        case "?801":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("VOL4", vTempSettingByte);
                            cVOL4 = vTempSettingByte;
                            return "Parameter was written";
                        case "?901":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("VOL5", vTempSettingByte);
                            cVOL5 = vTempSettingByte;
                            return "Parameter was written";
                        case "?502":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MOR1", vTempSettingBool);
                            cMOR1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?602":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MOR2", vTempSettingBool);
                            cMOR2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?702":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MOR3", vTempSettingBool);
                            cMOR3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?802":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MOR4", vTempSettingBool);
                            cMOR4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?902":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MOR5", vTempSettingBool);
                            cMOR5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?503":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("NOO1", vTempSettingBool);
                            cNOO1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?603":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("NOO2", vTempSettingBool);
                            cNOO2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?703":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("NOO3", vTempSettingBool);
                            cNOO3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?803":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("NOO4", vTempSettingBool);
                            cNOO4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?903":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("NOO5", vTempSettingBool);
                            cNOO5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?504":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("EVE1", vTempSettingBool);
                            cEVE1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?604":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("EVE2", vTempSettingBool);
                            cEVE2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?704":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("EVE3", vTempSettingBool);
                            cEVE3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?804":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("EVE4", vTempSettingBool);
                            cEVE4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?904":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("EVE5", vTempSettingBool);
                            cEVE5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?505":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MON1", vTempSettingBool);
                            cMON1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?605":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MON2", vTempSettingBool);
                            cMON2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?705":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MON3", vTempSettingBool);
                            cMON3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?805":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MON4", vTempSettingBool);
                            cMON4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?905":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("MON5", vTempSettingBool);
                            cMON5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?506":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("TUE1", vTempSettingBool);
                            cTUE1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?606":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("TUE2", vTempSettingBool);
                            cTUE2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?706":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("TUE3", vTempSettingBool);
                            cTUE3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?806":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("TUE4", vTempSettingBool);
                            cTUE4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?906":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("TUE5", vTempSettingBool);
                            cTUE5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?507":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("WED1", vTempSettingBool);
                            cWED1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?607":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("WED2", vTempSettingBool);
                            cWED2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?707":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("WED3", vTempSettingBool);
                            cWED3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?807":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("WED4", vTempSettingBool);
                            cWED4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?907":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("WED5", vTempSettingBool);
                            cWED5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?508":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("THU1", vTempSettingBool);
                            cTHU1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?608":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("THU2", vTempSettingBool);
                            cTHU2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?708":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("THU3", vTempSettingBool);
                            cTHU3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?808":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("THU4", vTempSettingBool);
                            cTHU4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?908":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("THU5", vTempSettingBool);
                            cTHU5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?509":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("FRI1", vTempSettingBool);
                            cFRI1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?609":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("FRI2", vTempSettingBool);
                            cFRI2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?709":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("FRI3", vTempSettingBool);
                            cFRI3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?809":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("FRI4", vTempSettingBool);
                            cFRI4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?909":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("FRI5", vTempSettingBool);
                            cFRI5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?510":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SAT1", vTempSettingBool);
                            cSAT1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?610":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SAT2", vTempSettingBool);
                            cSAT2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?710":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SAT3", vTempSettingBool);
                            cSAT3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?810":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SAT4", vTempSettingBool);
                            cSAT4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?910":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SAT5", vTempSettingBool);
                            cSAT5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?511":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SUN1", vTempSettingBool);
                            cSUN1 = vTempSettingBool;
                            return "Parameter was written";
                        case "?611":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SUN2", vTempSettingBool);
                            cSUN2 = vTempSettingBool;
                            return "Parameter was written";
                        case "?711":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SUN3", vTempSettingBool);
                            cSUN3 = vTempSettingBool;
                            return "Parameter was written";
                        case "?811":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SUN4", vTempSettingBool);
                            cSUN4 = vTempSettingBool;
                            return "Parameter was written";
                        case "?911":
                            vTempSettingBool = (vQueryPar == "TRU");
                            WriteSettingBool("SUN5", vTempSettingBool);
                            cSUN5 = vTempSettingBool;
                            return "Parameter was written";
                        case "?512":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("RAF1", vTempSettingByte);
                            cRAF1 = vTempSettingByte;
                            return "Parameter was written";
                        case "?612":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("RAF2", vTempSettingByte);
                            cRAF2 = vTempSettingByte;
                            return "Parameter was written";
                        case "?712":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("RAF3", vTempSettingByte);
                            cRAF3 = vTempSettingByte;
                            return "Parameter was written";
                        case "?812":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("RAF4", vTempSettingByte);
                            cRAF4 = vTempSettingByte;
                            return "Parameter was written";
                        case "?912":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("RAF5", vTempSettingByte);
                            cRAF5 = vTempSettingByte;
                            return "Parameter was written";
                        case "?513":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("GAF1", vTempSettingByte);
                            cGAF1 = vTempSettingByte;
                            return "Parameter was written";
                        case "?613":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("GAF2", vTempSettingByte);
                            cGAF2 = vTempSettingByte;
                            return "Parameter was written";
                        case "?713":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("GAF3", vTempSettingByte);
                            cGAF3 = vTempSettingByte;
                            return "Parameter was written";
                        case "?813":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("GAF4", vTempSettingByte);
                            cGAF4 = vTempSettingByte;
                            return "Parameter was written";
                        case "?913":
                            vTempSettingByte = Convert.ToByte(vQueryPar);
                            WriteSettingByte("GAF5", vTempSettingByte);
                            cGAF5 = vTempSettingByte;
                            return "Parameter was written";
                        // -------- unknown request code - 
                        default:
                            return "FAILURE_UNKNOWN";
                    }
                }
                else { return "FAILURE_UNKNOWN";}
                ;
            };
            fTcpServer.Initialise(8081);

            this.DebugLog("[APP]", "Information", "Initialisation finished, Task timer will be started", "The application has finished the initialization of its components and the background task timer will be started.");

            this.LogTimer = new Timer(this.LogTimer_Tick, null, 1000, 1000);
        }
    }
}