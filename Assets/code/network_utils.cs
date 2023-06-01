﻿using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.NetworkInformation;

#if STANDALONE_SERVER
#else
using UnityEngine;
#endif

public static class network_utils
{
    //###############################//
    // USED BOTH BY THE STANDALONE   //
    // SERVER AND THE IN-GAME SERVER //
    //###############################//

    /// <summary> Given a message type and a payload, compose a full message. </summary>
    public static byte[] compose_message(byte message_type, byte[] payload)
    {
        // Message is of form [type, payload_length, payload]
        return concat_buffers(
            new byte[] { message_type },
            encode_int(payload.Length),
            payload
        );
    }

    public delegate void message_handler(byte message_type, byte[] buffer, int offset, int payload_length);

    public static bool decompose_message(
        byte[] buffer, ref int offset, int data_bytes, out int initial_offset, message_handler handler)
    {
        // Record the message start, in case we get truncated
        initial_offset = offset;

        // Parse message type
        if (offset + 1 > data_bytes) return false;
        var msg_type = buffer[offset];
        offset += 1;

        // Parse payload length
        if (offset + sizeof(int) > data_bytes) return false;
        int payload_length = decode_int(buffer, ref offset);

        // Parse message
        if (offset + payload_length > data_bytes) return false;
        handler(msg_type, buffer, offset, payload_length);
        offset += payload_length;

        return true;
    }

    /// <summary> Concatinate the given byte arrays into a single byte array. </summary>
    public static byte[] concat_buffers(params byte[][] buffers)
    {
        int tot_length = 0;
        for (int i = 0; i < buffers.Length; ++i)
            tot_length += buffers[i].Length;

        int offset = 0;
        byte[] ret = new byte[tot_length];
        for (int i = 0; i < buffers.Length; ++i)
        {
            System.Buffer.BlockCopy(buffers[i], 0, ret, offset, buffers[i].Length);
            offset += buffers[i].Length;
        }

        return ret;
    }

    /// <summary> Encode an int ready to be sent over the network. </summary>
    public static byte[] encode_int(int i)
    {
        return System.BitConverter.GetBytes(i);
    }

    /// <summary> Decode an integer that was encoded using <see cref="encode_int(int)"/>.
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static int decode_int(byte[] buffer, ref int offset)
    {
        int i = System.BitConverter.ToInt32(buffer, offset);
        offset += sizeof(int);
        return i;
    }

    /// <summary> Encode a ulong ready to be sent over the network. </summary>
    public static byte[] encode_ulong(ulong i)
    {
        return System.BitConverter.GetBytes(i);
    }

    /// <summary> Decode a ulong that was encoded using <see cref="encode_ulong(ulong)"/>.
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static ulong decode_ulong(byte[] buffer, ref int offset)
    {
        ulong i = System.BitConverter.ToUInt64(buffer, offset);
        offset += sizeof(ulong);
        return i;
    }

    /// <summary> Encodes a bool ready to be sent over the network. </summary>
    public static byte[] encode_bool(bool b)
    {
        return System.BitConverter.GetBytes(b);
    }
    /// <summary> Decode a bool encoded using <see cref="encode_bool(bool)"/>.
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static bool decode_bool(byte[] buffer, ref int offset)
    {
        bool b = System.BitConverter.ToBoolean(buffer, offset);
        offset += sizeof(bool);
        return b;
    }

    /// <summary> Encode a float ready to be sent over the network. </summary>
    public static byte[] encode_float(float f)
    {
        return System.BitConverter.GetBytes(f);
    }

    /// <summary> Decode a float encoded with <see cref="encode_float(float)"/>.
    /// Increments offset by the number of bytes decoded. </summary>
    public static float decode_float(byte[] buffer, ref int offset)
    {
        float f = System.BitConverter.ToSingle(buffer, offset);
        offset += sizeof(float);
        return f;
    }

    /// <summary> Encode a string ready to be sent over the network (including it's length). </summary>
    public static byte[] encode_string(string str)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(str);
        return concat_buffers(encode_int(ascii.Length), ascii);
    }

    /// <summary> Decode a string encoded using <see cref="encode_string(string)"/>.
    /// <paramref name="offset"/> will be incremented by the number of bytes decoded. </summary>
    public static string decode_string(byte[] buffer, ref int offset)
    {
        int length = decode_int(buffer, ref offset);
        string str = System.Text.Encoding.ASCII.GetString(buffer, offset, length);
        offset += length;
        return str;
    }

    delegate int address_prioritizer(System.Net.IPAddress address);

    /// <summary> Get the ip address of the local machine, as used by a server. </summary>
    public static System.Net.IPAddress local_ip_address()
    {
        // Find the local ip address to listen on
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());

        var valid_addresses = new List<System.Net.IPAddress>();
        foreach (var ip in host.AddressList)
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                valid_addresses.Add(ip);

        var interfaces = new Dictionary<System.Net.IPAddress, NetworkInterface>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                if (valid_addresses.Contains(ip.Address))
                    interfaces[ip.Address] = ni;

        address_prioritizer prioritizer = (a) =>
        {
            // De-prioritize WSL
            if (interfaces[a].Name.ToLower().Contains("wsl")) return 2;

            // Prefer addresses starting with 192
            if (a.ToString().StartsWith("192")) return -1;

            // Normal priority
            return 0;
        };

        valid_addresses.Sort((a, b) => prioritizer(a).CompareTo(prioritizer(b)));

        if (valid_addresses.Count == 0)
            throw new System.Exception("No network adapters found!");

        return valid_addresses[0];
    }

    /// <summary> Class for monitoring network traffic </summary>
    public class traffic_monitor
    {
        int bytes_since_last;
        float time_last;
        float rate;
        float window_length;

        float time_since_start()
        {
#           if STANDALONE_SERVER
            return server.Time.realtimeSinceStartup;
#           else
            return Time.realtimeSinceStartup;
#           endif
        }

        /// <summary> Create a traffic monitor. </summary>
        public traffic_monitor(float window_length = 0.5f)
        {
            time_last = time_since_start();
            this.window_length = window_length;
        }

        /// <summary> Get a string reporting the usage (e.g 124.3 KB/s) </summary>
        public string usage()
        {
            // Update rate if sufficent time has passed
            float time = time_since_start();
            if (time - time_last > window_length)
            {
                rate = bytes_since_last / (time - time_last);
                bytes_since_last = 0;
                time_last = time;
            }

            if (rate < 1e3f) return System.Math.Round(rate, 2) + " B/s";
            if (rate < 1e6f) return System.Math.Round(rate / 1e3f, 2) + " KB/s";
            if (rate < 1e9f) return System.Math.Round(rate / 1e6f, 2) + " MB/s";
            if (rate < 1e12f) return System.Math.Round(rate / 1e9f, 2) + " GB/s";
            return "A lot";
        }

        public void log_bytes(int bytes) { bytes_since_last += bytes; }
    }

    public static string serial_info(byte[] serial)
    {
        string ret = "Length = " + serial.Length + " bytes = ";
        for (int i = 0; i < serial.Length && i < 10; ++i)
            ret += serial[i] + " ";
        if (serial.Length <= 10) return ret;
        return ret + "...";
    }


#if STANDALONE_SERVER

    //####################################//
    // USED ONLY BY THE STANDALONE SERVER //
    //####################################//

    /// <summary> Encode a vector3 ready to be sent over the network. </summary>
    public static byte[] encode_vector3(server.Vector3 v)
    {
        return concat_buffers(
            System.BitConverter.GetBytes(v.x),
            System.BitConverter.GetBytes(v.y),
            System.BitConverter.GetBytes(v.z)
        );
    }

    /// <summary> Decode a vector3 encoded using <see cref="encode_vector3(Vector3)"/>. 
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static server.Vector3 decode_vector3(byte[] buffer, ref int offset)
    {
        var vec = new server.Vector3(
            System.BitConverter.ToSingle(buffer, offset),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float)),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float) * 2)
        );
        offset += 3 * sizeof(float);
        return vec;
    }

#else

    //#################################//
    // USED ONLY BY THE IN-GAME SERVER //
    //#################################//

    /// <summary> Get a string displaying the given bytes. </summary>
    public static string byte_string(byte[] bytes, int offset = 0, int length = -1)
    {
        if (length < 0) length = bytes.Length;
        string ret = "";
        for (int i = 0; i < length; ++i)
            ret += bytes[offset + i] + ", ";
        ret = ret.Substring(0, ret.Length - 2);
        return ret;
    }

    /// <summary> Apply the function <paramref name="f"/> to 
    /// <paramref name="parent"/>, and all T in it's children. 
    /// Guaranteed to carry out in top-down order. </summary>
    public static void top_down<T>(Transform parent, do_func<T> f)
        where T : MonoBehaviour
    {
        Queue<Transform> to_do = new Queue<Transform>();
        to_do.Enqueue(parent);

        while (to_do.Count > 0)
        {
            var doing_to = to_do.Dequeue();

            foreach (Transform t in doing_to)
                to_do.Enqueue(t);

            var found = doing_to.GetComponent<T>();
            if (found != null) f(found);
        }
    }
    public delegate void do_func<T>(T t);

    /// <summary> Encode a vector3 ready to be sent over the network. </summary>
    public static byte[] encode_vector3(Vector3 v)
    {
        return concat_buffers(
            System.BitConverter.GetBytes(v.x),
            System.BitConverter.GetBytes(v.y),
            System.BitConverter.GetBytes(v.z)
        );
    }

    /// <summary> Decode a vector3 encoded using <see cref="encode_vector3(Vector3)"/>. 
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static Vector3 decode_vector3(byte[] buffer, ref int offset)
    {
        var vec = new Vector3(
            System.BitConverter.ToSingle(buffer, offset),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float)),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float) * 2)
        );
        offset += 3 * sizeof(float);
        return vec;
    }

    /// <summary> Encodes a quaternion ready to be sent over the network. </summary>
    public static byte[] encode_quaternion(Quaternion q)
    {
        return concat_buffers(
            System.BitConverter.GetBytes(q.x),
            System.BitConverter.GetBytes(q.y),
            System.BitConverter.GetBytes(q.z),
            System.BitConverter.GetBytes(q.w)
        );
    }

    /// <summary> Decode a quaternion encoded using <see cref="encode_quaternion(Quaternion)"/>.
    /// Offset will be incremented by the number of bytes decoded. </summary>
    public static Quaternion decode_quaternion(byte[] buffer, ref int offset)
    {
        Quaternion q = new Quaternion(
            System.BitConverter.ToSingle(buffer, offset),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float)),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float) * 2),
            System.BitConverter.ToSingle(buffer, offset + sizeof(float) * 3)
        );
        offset += sizeof(float) * 4;
        return q;
    }

#endif
}