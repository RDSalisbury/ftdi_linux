using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Sockets;
using FTD2XX_NET;


namespace ftdi20bridge
{
    class Program
    {
        static int listenPort = 0;
        static Dictionary<string, Tuple<Thread, CancellationTokenSource>> deviceThreads = new Dictionary<string, Tuple<Thread, CancellationTokenSource>>();

        static void Main(string[] args)
        {           
            if (args.Length == 0 || !int.TryParse(args[0], out listenPort))
                listenPort = 12345;

            
            TcpListener management = new TcpListener(IPAddress.Any, listenPort);
            management.Start();
            Console.WriteLine("Press escape to end program.");
            Console.WriteLine("Listening for connections on port {0}", listenPort);


            bool exitProgram = false;
            while (!exitProgram)
            {               
                if (Console.KeyAvailable)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                        exitProgram = true;
                }
                if (management.Pending())
                {
                    TcpClient conn = management.AcceptTcpClient();
                    Console.WriteLine("Connection from {0}", conn.Client.RemoteEndPoint.ToString());
                    conn.ReceiveBufferSize = 65536;
                    conn.SendBufferSize = 65536;
                    NetworkStream ns = conn.GetStream();
                    StreamReader nsIn = new StreamReader(ns, Encoding.ASCII);


                    string cmd = nsIn.ReadLine();

                    Console.WriteLine("received: {0}", cmd ?? "null");

                    if (cmd == null || cmd.Length == 0) {
                        break;
                    }

                    if (cmd[0] == '?')
                        sendIqpList(conn);
                    else if (cmd[0] == '@')
                        openSerialPort(conn, cmd.Substring(1));
                    else if (cmd[0] == '*')
                        listOpenPorts(conn);
                }

            }

            foreach(var devThreadInfo in deviceThreads.Values)
            {
                devThreadInfo.Item2.Cancel();
                devThreadInfo.Item1.Join();
            }

            management.Stop();
        }

        private static void listOpenPorts(TcpClient conn)
        {
            NetworkStream ns = conn.GetStream();
            StreamWriter nsOut = new StreamWriter(ns);
            nsOut.NewLine = "\n";
            nsOut.WriteLine("Open ports:");
            Console.WriteLine("Open ports:");
            foreach (string sn in deviceThreads.Keys)
            {
                var tinfo = deviceThreads[sn];
                if (tinfo.Item1.IsAlive)
                {
                    nsOut.WriteLine("{0}", sn);
                }
            }
            nsOut.WriteLine();
            nsOut.Flush();
            ns.Close();
            conn.Close();
        }

        static void sendIqpList(TcpClient conn)
        {
            NetworkStream ns = conn.GetStream();
            StreamWriter nsOut = new StreamWriter(ns);
            nsOut.NewLine = "\n";
            nsOut.WriteLine("IQPs found:");
            Console.WriteLine("IQPs found:");

            FTDI masterDevice = new FTDI();

            FTDI.FT_STATUS ret;

            uint devCount = 0;
            masterDevice.GetNumberOfDevices(ref devCount);

            FTDI.FT_DEVICE_INFO_NODE[] devList = new FTDI.FT_DEVICE_INFO_NODE[devCount];
            masterDevice.GetDeviceList(devList);

            foreach (FTDI.FT_DEVICE_INFO_NODE info in devList)
            {
                string portName = "?";
                ret = masterDevice.OpenBySerialNumber(info.SerialNumber);
                if (ret != FTDI.FT_STATUS.FT_OK)
                {
                    portName = "!";
                }
                else
                {
                    masterDevice.GetCOMPort(out portName);
                    masterDevice.Close();
                }
                
                nsOut.WriteLine("{0},{1},{2},{3}", info.Description, info.Type.ToString(), info.SerialNumber, portName);
                Console.WriteLine("{0},{1},{2},{3}", info.Description, info.Type.ToString(), info.SerialNumber, portName);
            }
            nsOut.WriteLine("");
            Console.WriteLine("");

            nsOut.Flush();
            ns.Close();
            conn.Close();
        }

        // returns true on success
        static bool openSerialPort(TcpClient conn, string snToConnect)
        {
            NetworkStream ns = conn.GetStream();
            ns.WriteTimeout = 10;
            ns.ReadTimeout = 10;
            byte[] msg;

            FTDI clientDevice = new FTDI();
            FTDI.FT_STATUS ftdiRet = clientDevice.OpenBySerialNumber(snToConnect);
            ftdiRet |= clientDevice.SetBaudRate(1250000);
            ftdiRet |= clientDevice.SetDataCharacteristics(FTDI.FT_DATA_BITS.FT_BITS_8, FTDI.FT_STOP_BITS.FT_STOP_BITS_2, FTDI.FT_PARITY.FT_PARITY_ODD);
            ftdiRet |= clientDevice.SetFlowControl(FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0, 0);
            ftdiRet |= clientDevice.SetTimeouts(8000, 100);
            ftdiRet |= clientDevice.InTransferSize(65536);
            ftdiRet |= clientDevice.SetDTR(true);

            if (ftdiRet != FTDI.FT_STATUS.FT_OK)
            {
                //throw new IOException("Failed to open FTDI device");
                Console.WriteLine("ERR: Failed to open FTDI device\n");
                msg = ASCIIEncoding.ASCII.GetBytes("ERR: Failed to open FTDI device\n");
                ns.Write(msg, 0, msg.Length);
                ns.Close();
                conn.Close();
                return false;
            }

            string desc = "X", sn = "X";
            clientDevice.GetDescription(out desc);
            clientDevice.GetSerialNumber(out sn);
            Console.WriteLine("OK: Opened FTDI device {0}-{1}", desc, sn);
            msg = ASCIIEncoding.ASCII.GetBytes("OK: Opened:" + snToConnect + "\n");
            ns.Write(msg, 0, msg.Length);


            CancellationTokenSource ct = new CancellationTokenSource();
            Thread deviceThread = new Thread((ParameterizedThreadStart)doServer);
            deviceThreads[snToConnect] = new Tuple<Thread, CancellationTokenSource>(deviceThread, ct);
            Tuple<FTDI, TcpClient, CancellationToken> threadArg = new Tuple<FTDI, TcpClient, CancellationToken>(clientDevice, conn, ct.Token);
            deviceThread.IsBackground = true;
            deviceThread.Start(threadArg);

            return true;
        }

        static void doServer(object argObj)
        {
            var arg = (Tuple<FTDI, TcpClient, CancellationToken>)argObj;
            FTDI clientDevice = arg.Item1;
            TcpClient clientConn = arg.Item2;
            NetworkStream clientStream = clientConn.GetStream();
            CancellationToken ct = arg.Item3;

            string ftdiSN = "";
            clientDevice.GetSerialNumber(out ftdiSN);
            if (ftdiSN != "")
                ftdiSN = "(" + ftdiSN + ")";

            uint bytesInDevice = 0;
            uint bytesDone = 0;
            int bytePos = 0;
            int bytesToWrite = 0;
            byte[] buf = new byte[1024 * 1024];
            byte[] buf2 = new byte[1024 * 1024]; // used when writing to FTDI, to start in the middle in case a write doesn't send all data

            while (true)
            {
                // Check if network connection has closed
                if (clientConn.Client.Poll(0, SelectMode.SelectRead) && !clientStream.DataAvailable)
                {
                    Console.WriteLine("Connection closed");
                    clientStream.Close();
                    clientConn.Close();
                    clientDevice.Close();
                    break;
                }

                // Check if we have been asked to end
                if (ct.IsCancellationRequested)
                {                    
                    Console.WriteLine("Program ending");
                    clientStream.Close();
                    clientConn.Close();
                    clientDevice.Close();
                    break;
                }

                // FTDI -> Network
                if (clientDevice.GetRxBytesAvailable(ref bytesInDevice) != FTDI.FT_STATUS.FT_OK)
                    throw new IOException("Failed to get number of RX bytes available in FTDI device.");

                if (bytesInDevice > 0)
                {
                    Console.WriteLine("FTDI{0} -> Network", ftdiSN);
                    Console.WriteLine("  {0} bytes from FTDI", bytesInDevice);
                    if (bytesInDevice > buf.Length)
                        bytesInDevice = (uint)buf.Length;

                    clientDevice.Read(buf, bytesInDevice, ref bytesDone);
                    clientStream.Write(buf, 0, (int)bytesDone);
                    Console.WriteLine("  Wrote {0} bytes to network", bytesDone);
                }

                // FTDI <- Network
                if (clientStream.DataAvailable)
                {
                    Console.WriteLine("FTDI{0} <- Network", ftdiSN);

                    bytePos = 0;
                    bytesToWrite = clientStream.Read(buf, 0, buf.Length);

                    Console.WriteLine("  {0} bytes from network", bytesToWrite);


                    clientDevice.Write(buf, bytesToWrite, ref bytesDone);
                    Console.WriteLine("  Wrote {0} bytes to FTDI", bytesDone);

                    /*
                    do
                    {
                        Array.Copy(buf, bytePos, buf2, 0, bytesToWrite);
                        clientDevice.Write(buf2, bytesToWrite, ref bytesDone);
                        Console.WriteLine("  Wrote {0} bytes to FTDI", bytesDone);

                        bytesToWrite -= (int)bytesDone;
                        bytePos += (int)bytesDone;
                    } while (bytesToWrite > 0);
                    */



                }

                //Thread.Yield();


            }
        }

    }
    

}
