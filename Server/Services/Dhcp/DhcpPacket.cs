#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace PacketDotNet.Dhcp
{
    public enum DhcpMessageType : byte
    {
        Discover = 1,
        Offer = 2,
        Request = 3,
        Decline = 4,
        Ack = 5,
        Nak = 6,
        Release = 7,
        Inform = 8
    }

    public sealed class DhcpPacket
    {
        public byte Op;
        public byte Htype;
        public byte Hlen;
        public byte Hops;
        public uint Xid;
        public ushort Secs;
        public ushort Flags;

        public IPAddress CiAddr = IPAddress.Any;
        public IPAddress YiAddr = IPAddress.Any;     // your IP (сервер выдаёт клиенту)
        public IPAddress SiAddr = IPAddress.Any;
        public IPAddress GiAddr = IPAddress.Any;

        public byte[] ChAddr = new byte[16];

        public Dictionary<byte, byte[]> Options = new();

        public DhcpPacket()
        {
        }

        public DhcpPacket(byte[] raw)
        {
            Parse(raw);
        }

        // ====================== PARSE =======================
        private void Parse(byte[] raw)
        {

            if (raw == null)
                throw new ArgumentNullException(nameof(raw));
        
            if (raw.Length < 240)
                throw new Exception("Invalid DHCP packet");

            Op = raw[0];
            Htype = raw[1];
            Hlen = raw[2];
            Hops = raw[3];
            Xid = BitConverter.ToUInt32(raw, 4);
            Secs = BitConverter.ToUInt16(raw, 8);
            Flags = BitConverter.ToUInt16(raw, 10);

            CiAddr = new IPAddress(raw.Skip(12).Take(4).ToArray());
            YiAddr = new IPAddress(raw.Skip(16).Take(4).ToArray());
            SiAddr = new IPAddress(raw.Skip(20).Take(4).ToArray());
            GiAddr = new IPAddress(raw.Skip(24).Take(4).ToArray());

            Array.Copy(raw, 28, ChAddr, 0, 16);

            // Options start at byte 240
            int pos = 240;
            while (pos + 1 < raw.Length)
            {
                byte opt = raw[pos++];
                if (opt == 255) break; // END

                byte len = raw[pos++];
                var data = raw.Skip(pos).Take(len).ToArray();
                pos += len;

                Options[opt] = data;
            }
        }

        // ====================== BUILD ========================

        public byte[] Bytes
        {
            get
            {
                var buf = new List<byte>();

                buf.Add(Op);
                buf.Add(Htype);
                buf.Add(Hlen);
                buf.Add(Hops);

                buf.AddRange(BitConverter.GetBytes(Xid));
                buf.AddRange(BitConverter.GetBytes(Secs));
                buf.AddRange(BitConverter.GetBytes(Flags));

                buf.AddRange(CiAddr.GetAddressBytes());
                buf.AddRange(YiAddr.GetAddressBytes());
                buf.AddRange(SiAddr.GetAddressBytes());
                buf.AddRange(GiAddr.GetAddressBytes());

                // CHADDR = 16 bytes
                var ch = new byte[16];
                Array.Copy(ChAddr, ch, Math.Min(ChAddr.Length, 16));
                buf.AddRange(ch);

                // server name
                buf.AddRange(new byte[64]);
                buf.AddRange(new byte[128]);

                // magic cookie
                buf.AddRange(new byte[] { 99, 130, 83, 99 });

                // options
                foreach (var kv in Options)
                {
                    buf.Add(kv.Key);
                    buf.Add((byte)kv.Value.Length);
                    buf.AddRange(kv.Value);
                }

                buf.Add(255); // END

                return buf.ToArray();
            }
        }

        // ====================== HELPERS ======================

        public DhcpMessageType GetMsgType()
        {
            if (Options.TryGetValue(53, out var data))
                return (DhcpMessageType)data[0];

            throw new Exception("DHCP msg type missing");
        }

        public IPAddress? GetRequestedIpAddress()
        {
            if (!Options.TryGetValue(50, out var data)) return null;
            return new IPAddress(data);
        }

        public void SetMsgType(DhcpMessageType type) =>
            Options[53] = new[] { (byte)type };

        public void SetOptionSubnetMask(IPAddress mask) =>
            Options[1] = mask.GetAddressBytes();

        public void SetOptionRouter(IPAddress gw) =>
            Options[3] = gw.GetAddressBytes();

        public void SetOptionLeaseTime(uint seconds) =>
            Options[51] = BitConverter.GetBytes(seconds).Reverse().ToArray();

        public void SetOptionServerIdentifier(IPAddress ip) =>
            Options[54] = ip.GetAddressBytes();
    }
}
