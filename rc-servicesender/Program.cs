using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Nefarius.ViGEm.Client;

namespace rc_servicesender
{
    class Program
    {
        public struct rc_msg
        {
            /* typs is MSB, idex is LSB */
            public byte type_idex;
            /* message frame index */
            public byte frame_idx;
            /* sbus or ppm data */
            public byte[] rc_data; // 25;
        };

        public static int SwapEndianness(int value)
        {
            var b1 = (value >> 0) & 0xff;
            var b2 = (value >> 8) & 0xff;
            var b3 = (value >> 16) & 0xff;
            var b4 = (value >> 24) & 0xff;

            return b1 << 24 | b2 << 16 | b3 << 8 | b4 << 0;
        }

        static UInt16 F(int v, byte s)
        {
           // v = SwapEndianness(v);
            return (UInt16)(((v) >> (s)) & 0x7ff);
        }


        static public UInt16[] decode(byte[] s)
        {
            UInt16[] d = new UInt16[16];
            /* unroll channels 1-8 */
            d[0] = F(s[0] | s[1] << 8, 0);
            d[1] = F(s[1] | s[2] << 8, 3);
            d[2] = F(s[2] | s[3] << 8 | s[4] << 16, 6);
            d[3] = F(s[4] | s[5] << 8, 1);
            d[4] = F(s[5] | s[6] << 8, 4);
            d[5] = F(s[6] | s[7] << 8 | s[8] << 16, 7);
            d[6] = F(s[8] | s[9] << 8, 2);
            d[7] = F(s[9] | s[10] << 8, 5);

            /* unroll channels 9-16 */
            d[8] = F(s[11] | s[12] << 8, 0);
            d[9] = F(s[12] | s[13] << 8, 3);
            d[10] = F(s[13] | s[14] << 8 | s[15] << 16, 6);
            d[11] = F(s[15] | s[16] << 8, 1);
            d[12] = F(s[16] | s[17] << 8, 4);
            d[13] = F(s[17] | s[18] << 8 | s[19] << 16, 7);
            d[14] = F(s[19] | s[20] << 8, 2);
            d[15] = F(s[20] | s[21] << 8, 5);

            return d;
        }

        public static double mapConstrained(double x, double in_min, double in_max, double out_min, double out_max)
        {
            var output = (x - in_min) * (out_max - out_min) / (in_max - in_min) + out_min;
            output = Math.Max(output, out_min);
            output = Math.Min(output, out_max);
            return output;
        }

        static void Main(string[] args)
        {
            var clientjoy = new ViGEmClient();
            var joy = clientjoy.CreateXbox360Controller();
            joy.Connect();

            foreach (var a in MulticastService.GetIPAddresses())
            {
                Console.WriteLine($"IP address {a}");
            }

            var addresses = MulticastService.GetIPAddresses()
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            var mdns = new MulticastService((nics) => { return nics.Where(nic => nic.SupportsMulticast && nic.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().GatewayAddresses.Count > 0); });

            mdns.QueryReceived += (s, e) =>
            {
                var names = e.Message.Questions
                    .Select(q => q.Name + " " + q.Type);
                Console.WriteLine($"mdns got a query for {String.Join(", ", names)} {e.RemoteEndPoint}");
            };
            mdns.AnswerReceived += (s, e) =>
            {
                var names = e.Message.Answers
                    .Select(q => q.Name + " " + q.Type)
                    .Distinct();
                Console.WriteLine($"mdns got answer for {String.Join(", ", names)} {e.RemoteEndPoint}");
            };
            mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    Console.WriteLine($"mdns discovered NIC '{nic.Name}' {nic.Description}");
                }

            };
            mdns.UseIpv4 = true;
            mdns.UseIpv6 = false;


            mdns.Start();


            var service = new ServiceProfile("HereLink-" + Environment.MachineName, "_mavlink._udp", 14550);//, addresses);
            var sd = new ServiceDiscovery(mdns);
            sd.Advertise(service);

            sd.ServiceDiscovered += (s, dn) => { Console.WriteLine($"sd discovered '{dn.ToCanonical()}'"); };

            Task.Run(() => {
                while (true)
                {
                    sd.Announce(service);
                    Thread.Sleep(30000);
                }
            });

            var client = new UdpClient(16666);

            IPEndPoint addr = IPEndPoint.Parse("0.0.0.0");

            byte[] data;

            while (true)
            {
                // echo it back
                try
                {
                    data = client.Receive(ref addr);

                    var list = decode(data.Skip(3).ToArray());

                    Console.WriteLine("SBUS {0} {1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}\t{12}\t{13}\t{14}\t{15}\t{16} ", data[0], list[0], list[1], list[2], list[3],
                        list[4], list[5], list[6], list[7], list[8], list[9], list[10], list[11], list[12], list[13], list[14], list[15]);

                    if (data[0] == 32)
                    {
                        //1024
                        //364 - 1684
                        joy.SetAxisValue(0, (short)mapConstrained(list[0], 364, 1684, short.MinValue, short.MaxValue));
                        joy.SetAxisValue(1, (short)mapConstrained(list[1], 364, 1684, short.MinValue, short.MaxValue));
                        joy.SetAxisValue(2, (short)mapConstrained(list[2], 364, 1684, short.MinValue, short.MaxValue));
                        joy.SetAxisValue(3, (short)mapConstrained(list[3], 364, 1684, short.MinValue, short.MaxValue));
                        joy.SetSliderValue(0, (byte)mapConstrained(list[4], 364, 1684, 0, 255));

                        joy.SetButtonState(5, list[5] > 1024 ? true : false);
                        joy.SetButtonState(6, list[6] > 1024 ? true : false);
                        joy.SetButtonState(7, list[7] > 1024 ? true : false);
                        joy.SetButtonState(8, list[8] > 1024 ? true : false);
                        joy.SetButtonState(9, list[9] > 1024 ? true : false);
                        joy.SetButtonState(10, list[10] > 1024 ? true : false);
                        joy.SetButtonState(11, list[11] > 1024 ? true : false);
                        joy.SetButtonState(12, list[12] > 1024 ? true : false);
                        joy.SetButtonState(13, list[13] > 1024 ? true : false);
                        joy.SetButtonState(14, list[14] > 1024 ? true : false);
                    }

                    //addr.Port = 16666;
                    //client.Send(data, data.Length, addr);
                }
                catch
                {

                }
            }
        }
    }
}
