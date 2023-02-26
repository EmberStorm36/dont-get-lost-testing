﻿using System.Collections;
using System.Collections.Generic;

#if STANDALONE_SERVER
#else
using UnityEngine;
#endif

public static class client
{
    /// <summary> Message types sent by the client. </summary>
    public enum MESSAGE : byte
    {
        // Numbering starts a specific number so erroneous 0's are caught
        LOGIN = 20,           // Client has logged in
        HEARTBEAT,           // Heartbeat response
        DISCONNECT,          // Disconnect this client
        CREATE,              // Create an object on the server
        DELETE,              // Delete an object from the server
        RENDER_RANGE_UPDATE, // Client render range has changed
        VARIABLE_UPDATE,     // A networked_variable has changed
        TRIGGER,             // A networked event has been triggered
        KICK,                // A player kick has been requested
        JOINABLE_QUERY,      // A query if a server is joinable
    }

    public const MESSAGE LARGEST_VALUE_MESSAGE = MESSAGE.KICK;

#if STANDALONE_SERVER
#else
    // Stuff below here is not used by the standalone server

    /// <summary> How many milliseconds to wait when attempting to connect. </summary>
    public const int CONNECTION_TIMEOUT_MS = 2000;

    // STATE VARIABLES //

    static int last_local_id;
    static float last_ping;
    static KeyValuePair<int, float> last_heartbeat;
    static bool activity_since_heartbeat;
    static network_utils.traffic_monitor traffic_up;
    static network_utils.traffic_monitor traffic_down;
    static Queue<pending_message> message_queue;
    static Queue<pending_creation_message> pending_creation_messages;
    static disconnect_func on_disconnect;
    static client_backend backend;
    static int last_server_time;
    static int last_server_time_local;
    static Dictionary<string, player_info> player_infos;
    static HashSet<networked_variable> queued_variable_updates;

    /// <summary> Any bytes of a partial message from the server that appeared at the
    /// end of a buffer, to be glued to the start of the next buffer. </summary>
    static byte[] truncated_read_message;

    // END STATE VARIABLES //

    public static int server_time => ((int)Time.realtimeSinceStartup - last_server_time_local) + last_server_time;
    public static float time_since_last_heartbeat => Time.realtimeSinceStartup - last_server_time_local;

    public delegate void callback();
    static callback heartbeat_callbacks;
    public static void add_heartbeat_callback(callback c) { heartbeat_callbacks += c; }

    /// <summary> Struct containing information about the players on the server. </summary>
    public class player_info
    {
        public Vector3 position;
        public bool connected;
    }

    /// <summary> Get information about player that are, or have been
    /// connected during this session. </summary>
    public static player_info get_player_info(string username)
    {
        // Try with name as given
        if (player_infos.TryGetValue(username, out player_info pi)) return pi;

        // Try with name with underscores replaced with spaces
        username = username.Replace('_', ' ');
        if (player_infos.TryGetValue(username, out pi)) return pi;

        // Try with name with each word capitalized
        username = username.capitalize_each_word();
        if (player_infos.TryGetValue(username, out pi)) return pi;

        return null;
    }

    /// <summary> Called when the client disconnects. </summary>
    /// <param name="message">The message from the server, if it 
    /// caused the disconnect, null otherwise.</param>
    public delegate void disconnect_func(string message);

    /// <summary> A message waiting to be sent. </summary>
    struct pending_message
    {
        public byte[] bytes;
        public float time_sent;
    }

    /// <summary> A message about creation waiting to be sent. </summary>
    struct pending_creation_message
    {
        public int parent_id;
        public string prefab;
        public networked creating;
    }

    /// <summary> Is the client connected to a server. </summary>
    public static bool connected { get => backend != null; }

    /// <summary> Call to prevent inactivity timeout. </summary>
    public static void register_activity() { activity_since_heartbeat = true; }

    /// <summary> Create a networked object on the client. Automatically lets the
    /// server know, which will syncronise the object (including it's existance)
    /// on other clients. </summary>
    /// <param name="position">The position to create the object at.</param>
    /// <param name="prefab">The prefab to create.</param>
    /// <param name="parent">The parent of the object to crreate</param>
    /// <param name="rotation">The rotation to create the object with.</param>
    /// <param name="network_id">The network id to create the object with. Negative values
    /// will be assigned a unique negative local id and await a network wide id (this covers
    /// most use cases). Positive values should only be used if we are sure that they 
    /// already exist on the server and should be associated with the object that we 
    /// are creating. </param>
    /// <returns>The created networked object.</returns>
    public static networked create(
        Vector3 position,
        string prefab,
        networked parent = null,
        Quaternion rotation = default,
        int network_id = -1)
    {
        // Instantiate the local object, but keep the name
        var created = networked.look_up(prefab);
        string name = created.name;
        created = Object.Instantiate(created);
        created.name = name;

        if (network_id < 0)
        {
            // Assign a (negative) unique local id
            created.network_id = --last_local_id;
        }
        else
        {
            // Copy the given network id
            created.network_id = network_id;
        }
        created.init_network_variables();

        // Parent if requested
        if (parent != null)
            created.transform.SetParent(parent.transform);

        // Create the object with the desired position + rotation
        if (rotation.Equals(default)) rotation = Quaternion.identity;
        created.networked_position = position;
        created.transform.rotation = rotation;

        // Get the id of my parent
        int parent_id = 0;
        if (parent != null) parent_id = parent.network_id;
        if (parent_id < 0) throw new System.Exception("Cannot create children of unregistered objects!");

        // Queue the creation message before on_create is called, so that 
        // we can safely create children in the on_create method (ensure
        // child creation messaage will arrive after my creation message).
        pending_creation_messages.Enqueue(new pending_creation_message
        {
            creating = created,
            parent_id = parent_id,
            prefab = prefab
        });

        // This is being created by this client
        // so must be the first time/it must have
        // authority
        created.on_first_create();
        created.on_create();

        if (parent != null)
        {
            parent.on_add_networked_child(created);
            foreach (var e in parent.GetComponents<IExtendsNetworked>())
                e.get_callbacks().on_add_networked_child?.Invoke(created);
        }
        return created;
    }

    /// <summary> Create an object as instructred by a server message, stored in the given buffer. </summary>
    static void create_from_network(byte[] buffer, int offset, int length)
    {
        // Record where the end of the serialization is
        int end = offset + length;

        // Deserialize info needed to reproduce the object
        int network_id = network_utils.decode_int(buffer, ref offset);
        int parent_id = network_utils.decode_int(buffer, ref offset);
        string prefab = network_utils.decode_string(buffer, ref offset);

        // Find the requested parent
        networked parent = parent_id > 0 ? networked.find_by_id(parent_id) : null;

        // Create the reproduction
        var nw = networked.look_up(prefab);
        string name = nw.name;
        nw = Object.Instantiate(nw);
        nw.transform.SetParent(parent?.transform);
        nw.name = name;
        nw.network_id = network_id;
        nw.init_network_variables();

        // Local rotation is intialized to the identity. If rotation
        // is variable, the user should implement that.
        nw.transform.localRotation = Quaternion.identity;

        // The rest is network variables that need deserializing
        int index = 0;
        while (offset < end)
        {
            int nv_length = network_utils.decode_int(buffer, ref offset);
            nw.variable_update(index, buffer, offset, nv_length);
            offset += nv_length;
            index += 1;
        }

        nw.on_create();

        if (parent != null)
        {
            parent.on_add_networked_child(nw);
            foreach (var e in parent.GetComponents<IExtendsNetworked>())
                e.get_callbacks().on_add_networked_child?.Invoke(nw);
        }
    }

    /// <summary> Send all of the messages currently queued. </summary>
    static void send_queued_messages()
    {
        // Send queued variable updates
        var update_next_frame = new HashSet<networked_variable>();
        foreach (var nv in queued_variable_updates)
        {
            if (!nv.owner_set)
            {
                Debug.LogError("Networked variable without an owner is sending updates. If you " +
                               "wish to use networked variables in this way send_updates must be " +
                               "set to false!");
                continue;
            }

            if (nv.network_id <= 0)
            {
                // Not yet registered, wait until next frame
                update_next_frame.Add(nv);
                continue;
            }

            // Send the update
            queue_message(MESSAGE.VARIABLE_UPDATE, nv.network_id, nv.index, nv.serialization());
        }

        // Forget any variables who have had updates sent
        queued_variable_updates = update_next_frame;

        // The buffer used to send messages
        byte[] send_buffer = new byte[backend.SendBufferSize];
        int offset = 0;
        var stream = backend.stream;

        // Send the message queue
        while (message_queue.Count > 0)
        {
            var msg = message_queue.Dequeue();

            if (msg.bytes.Length > backend.SendBufferSize)
                throw new System.Exception("Message too large!");

            if (offset + msg.bytes.Length > send_buffer.Length)
            {
                // Message would overrun buffer, send the
                // buffer and obtain a new one
                traffic_up.log_bytes(offset);
                try
                {
                    stream.Write(send_buffer, 0, offset);
                }
                catch (System.Exception e)
                {
                    disconnect(false, "Write failed, connection forcibly closed: " + e.Message);
                    return;
                }
                send_buffer = new byte[backend.SendBufferSize];
                offset = 0;
            }

            // Copy the message into the send buffer
            System.Buffer.BlockCopy(msg.bytes, 0, send_buffer, offset, msg.bytes.Length);
            offset += msg.bytes.Length; // Move to next message
        }

        // Send the buffer
        if (offset > 0)
        {
            traffic_up.log_bytes(offset);
            try
            {
                stream.Write(send_buffer, 0, offset);
            }
            catch (System.Exception e)
            {
                disconnect(false, "Write failed, connection forcibly closed: " + e.Message);
                return;
            }
        }
    }

    /// <summary> Register that the given variable needs to send an update message. </summary>
    public static void queue_variable_update(networked_variable v)
    {
        queued_variable_updates.Add(v);
    }

    /// <summary> Trigger network <paramref name="event_number"/> for
    /// the object with the given <paramref name="network_id"/>. </summary>
    public static void send_trigger(int network_id, int event_number)
    {
        queue_message(MESSAGE.TRIGGER, network_id, event_number);
    }

    /// <summary> Called when the render range of a player changes. </summary>
    public static void on_render_range_change(networked_player player)
    {
        queue_message(MESSAGE.RENDER_RANGE_UPDATE, player);
    }

    /// <summary> Called when an object is deleted on this client, sends the server that info. </summary>
    public static void on_delete(networked deleted, bool response_required)
    {
        queue_message(MESSAGE.DELETE, deleted.network_id, response_required);
    }

    /// <summary> Connect to a server running on the local machine. </summary>
    public static void connect_local(string username, ulong user_id, disconnect_func on_disconnect)
    {
        if (!server.started)
        {
            on_disconnect("Could not connect to local server (server not started).");
            return;
        }

        backend = new local_client_backend();
        connect(username, user_id, on_disconnect);
    }

    /// <summary> Connect directly to a host+port </summary>
    public static void direct_connect(string host, int port, string username, ulong user_id, disconnect_func on_disconnect)
    {
        backend = tcp_client_backend.connect(host, port);

        if (backend == null)
        {
            on_disconnect("Could not connect to remote server");
            return;
        }

        connect(username, user_id, on_disconnect);
    }

#if FACEPUNCH_STEAMWORKS

    /// <summary> Connect to a steam friend. </summary>
    public static void steam_connect(Steamworks.SteamId id_to_join,
        string my_username, ulong user_id, disconnect_func on_disconnect)
    {
        backend = new steamworks_client_backend(id_to_join, steamworks_server_backend.CLIENT_TO_SERVER_CHANNEL);
        connect(my_username, user_id, on_disconnect);
    }

#endif

    /// <summary> Connect the client to a server. </summary>
    static void connect(string username, ulong user_id, disconnect_func on_disconnect)
    {
        if (user_id == 0) throw new System.Exception("Tried to connect a user_id of 0!");

        // Initialize client state
        last_local_id = 0;
        last_ping = 0;
        last_heartbeat = default;
        traffic_up = new network_utils.traffic_monitor();
        traffic_down = new network_utils.traffic_monitor();
        message_queue = new Queue<pending_message>();
        pending_creation_messages = new Queue<pending_creation_message>();
        queued_variable_updates = new HashSet<networked_variable>();
        player_infos = new Dictionary<string, player_info>();
        client.on_disconnect = on_disconnect;

        // Initialize the networked object static state
        networked.client_initialize();

        // Ensure startup info is synced with what was actually used to connect
        game.startup.user_id = user_id;
        game.startup.username = username;

        // Send login message
        Debug.Log("Sending LOGIN message with username: " + username + " id: " + user_id);
        queue_message(MESSAGE.LOGIN, username, user_id, version_control.info.version);

        // Stop immediate server timeout
        last_server_time_local = (int)Time.realtimeSinceStartup;
    }

    public class join_query
    {
        client_backend backend;
        float start_time;

        public join_query(client_backend backend)
        {
            // Send a join query
            this.backend = backend;
            var buff = network_utils.compose_message((byte)MESSAGE.JOINABLE_QUERY, new byte[0]);
            backend.stream.Write(buff, 0, buff.Length);
            start_time = Time.time;
        }

        void process_response(byte type_byte, byte[] buffer, int offset, int length)
        {
            if (type_byte != (byte)server.MESSAGE.JOINABLE_RESPONSE)
                throw new System.Exception("Wrong response to join query!");

            _query_complete = true;
            result = network_utils.decode_bool(buffer, ref offset);
            close();
        }

        public void update()
        {
            if (backend == null)
            {
                _query_complete = true;
                return;
            }

            backend.Update();

            var stream = backend.stream;
            if (stream == null)
            {
                _query_complete = true;
                close();
                return;
            }

            // Attempt to read data
            while (stream.DataAvailable)
            {
                byte[] buffer = new byte[backend.ReceiveBufferSize];
                int read = stream.Read(buffer, 0, buffer.Length);
                int offset = 0;

                if (!network_utils.decompose_message(
                    buffer, ref offset, read, out int ignored, process_response))
                    throw new System.Exception("Truncated message in join query!");
            }
        }

        public bool query_complete
        {
            get
            {
                update();

                // Timeout
                if (Time.time - start_time > 5f)
                    _query_complete = true;

                return _query_complete;
            }
        }
        bool _query_complete = false;

        public bool result
        {
            get;
            private set;
        }

        public void close()
        {
            if (backend == null)
                return;
            backend.Close();
            backend = null;
        }
    }

    public static void disconnect(bool initiated_by_client, string msg_from_server = null, bool delete_player = false)
    {
        if (!connected) return; // Not connected

        if (initiated_by_client)
        {
            // Send any queued messages (including a disconnect message)
            queue_message(MESSAGE.DISCONNECT, delete_player);
            send_queued_messages();
        }

        try
        {
            // Close the stream (with a timeout so the above messages can be sent)
            backend.Close((int)(server.CLIENT_TIMEOUT * 1000));
            backend = null;
        }
        catch
        {
            Debug.Log("Connection severed ungracefully.");
        }

        on_disconnect(msg_from_server);
    }

    public static void update()
    {
        if (!connected) return;

        backend.Update();

        // Get the network stream
        backend_stream stream = null;
        try
        {
            stream = backend.stream;
        }
        catch (System.InvalidOperationException e)
        {
            disconnect(false, e.Message);
            return;
        }

        // Timeout server if we haven't had a heartbeat in a while
        /*
        if (time_since_last_heartbeat > server.CLIENT_HEARTBEAT_PERIOD * 10f)
        {
            disconnect(false, "No server heartbeat!");
            return;
        }
        */

        // Read messages to the client
        while (stream.DataAvailable)
        {
            byte[] buffer = new byte[backend.ReceiveBufferSize];

            // The start point in the buffer where new messages should be read into
            int buffer_start = 0;

            if (truncated_read_message != null)
            {
                // Glue a truncated message onto the start of the buffer
                System.Buffer.BlockCopy(truncated_read_message, 0,
                    buffer, 0, truncated_read_message.Length);
                buffer_start = truncated_read_message.Length;
                truncated_read_message = null;
            }

            // Read new bytes into the buffer
            int bytes_read = stream.Read(buffer, buffer_start,
                buffer.Length - buffer_start);
            traffic_down.log_bytes(bytes_read);

            // Work out how much data is in the buffer (including data
            // potentially copied from a previous truncation) and
            // initialze reading at the beginning.
            int data_bytes = bytes_read + buffer_start;
            int offset = 0;

            while (offset < data_bytes)
            {
                if (!network_utils.decompose_message(
                    buffer, ref offset, data_bytes,
                    out int last_message_start, parse_message))
                {
                    // Message truncated
                    // Save the truncated message to be glued at the start of the next buffer.
                    truncated_read_message = new byte[data_bytes - last_message_start];
                    System.Buffer.BlockCopy(buffer, last_message_start,
                        truncated_read_message, 0, truncated_read_message.Length);
                    break;
                }

                // The last message parsed caused a disconnect, we
                // should stop immedately as we can no longer send
                // messages to the server.
                if (!connected) return;
            }
        }

        // Queue pending creation messages (delayed until now
        // so that for each created object we have the most
        // up-to-date network variables).
        while (pending_creation_messages.Count > 0)
        {
            var cm = pending_creation_messages.Dequeue();

            if (cm.creating == null)
                continue; // This object has since been deleted

            // Request creation on the server
            queue_message(MESSAGE.CREATE, cm.creating.network_id,
                cm.parent_id, cm.prefab, cm.creating.serialize_networked_variables());
        }

        // Run network_update for each object
        networked.network_updates();

        // If it's been long enough, send another heartbeat
        if (Time.realtimeSinceStartup - last_heartbeat.Value > server.CLIENT_HEARTBEAT_PERIOD)
            queue_message(MESSAGE.HEARTBEAT);

        // Send messages
        send_queued_messages();
    }

    public static void kick(string username)
    {
        queue_message(MESSAGE.KICK, username);
    }

    public static string info()
    {
        if (!connected) return "Client not connected.";

        // Convert ping to string
        string ping = (last_ping * 1000) + " ms";
        if (last_ping < 0) ping = "> " + server.CLIENT_HEARTBEAT_PERIOD * 1000 + " ms";

        return "Client connected to " + backend.remote_address + "\n" +
               "    Objects            : " + networked.object_count + "\n" +
               "    Recently forgotten : " + networked.recently_forgotten_count + "\n" +
               "    Queued updates     : " + queued_variable_updates.Count + "\n" +
               "    Upload             : " + traffic_up.usage() + "\n" +
               "    Download           : " + traffic_down.usage() + "\n" +
               "    Effective ping     : " + ping + "\n" +
               "    Server time        : " + server_time + " (last = " + last_server_time + ")\n" +
               "    Last srv heartbeat : " + time_since_last_heartbeat + " seconds ago\n" +
               "    Activity           : " + activity_since_heartbeat + "\n";

    }

    public static string connected_player_info()
    {
        string ret = "";
        foreach (var kv in player_infos)
            ret += "    " + kv.Key + " " +
                  (kv.Value.connected ? "connected" : "disconnected") +
                 " at " + kv.Value.position + "\n";
        return ret;
    }

    //###########//
    // MESSAGING //
    //###########//

    /// <summary> Queue a message of the given <paramref name="type"/> 
    /// and <paramref name="args"/> to the server. </summary>
    static void queue_message(MESSAGE type, params object[] args)
    {
        // Send a message type + payload
        void send(MESSAGE msg_type, byte[] payload)
        {
            message_queue.Enqueue(new pending_message
            {
                bytes = network_utils.compose_message((byte)msg_type, payload),
                time_sent = Time.realtimeSinceStartup
            });
        }

        switch (type)
        {
            case MESSAGE.LOGIN:
                if (args.Length != 3)
                    throw new System.ArgumentException("Wrong number of arguments!");

                string uname = (string)args[0];
                ulong id = (ulong)args[1];
                string client_ver = (string)args[2];

                // Send the username + hashed password to the server
                send(MESSAGE.LOGIN, network_utils.concat_buffers(
                    network_utils.encode_string(uname),
                    network_utils.encode_ulong(id),
                    network_utils.encode_string(client_ver)));
                break;

            case MESSAGE.HEARTBEAT:
                if (args.Length != 0)
                    throw new System.Exception("Wrong number of arguments!");

                // Increment the heartbeat key, and record the send time
                last_heartbeat = new KeyValuePair<int, float>(
                    last_heartbeat.Key + 1,
                    Time.realtimeSinceStartup
                );

                send(MESSAGE.HEARTBEAT, network_utils.concat_buffers(
                    network_utils.encode_bool(activity_since_heartbeat),
                    network_utils.encode_int(last_heartbeat.Key)));

                // Reset activity monitor
                activity_since_heartbeat = false;
                break;

            case MESSAGE.DISCONNECT:
                if (args.Length != 1)
                    throw new System.ArgumentException("Wrong number of arguments!");

                bool delete_player = (bool)args[0];
                send(MESSAGE.DISCONNECT, network_utils.encode_bool(delete_player));
                break;

            case MESSAGE.CREATE:
                if (args.Length != 4)
                    throw new System.ArgumentException("Wrong number of arguments!");

                int local_id = (int)args[0];
                int parent_id = (int)args[1];
                string prefab = (string)args[2];
                byte[] variable_serializations = (byte[])args[3];

                send(MESSAGE.CREATE, network_utils.concat_buffers(
                    network_utils.encode_int(local_id),
                    network_utils.encode_int(parent_id),
                    network_utils.encode_string(prefab),
                    variable_serializations
                ));
                break;

            case MESSAGE.DELETE:
                if (args.Length != 2)
                    throw new System.ArgumentException("Wrong number of arguments!");

                int network_id = (int)args[0];
                if (network_id < 0)
                    throw new System.Exception("Tried to delete an unregistered object!");

                bool response_required = (bool)args[1];

                send(MESSAGE.DELETE, network_utils.concat_buffers(
                    network_utils.encode_int(network_id),
                    network_utils.encode_bool(response_required)
                ));
                break;

            case MESSAGE.RENDER_RANGE_UPDATE:
                if (args.Length != 1)
                    throw new System.ArgumentException("Wrong number of arguments!");

                var nw = (networked_player)args[0];

                // Send the new render range
                send(MESSAGE.RENDER_RANGE_UPDATE, network_utils.concat_buffers(
                    network_utils.encode_float(nw.render_range)
                ));
                break;

            case MESSAGE.VARIABLE_UPDATE:
                if (args.Length != 3)
                    throw new System.ArgumentException("Wrong number of arguments!");

                network_id = (int)args[0];
                int index = (int)args[1];
                byte[] serialization = (byte[])args[2];

                send(MESSAGE.VARIABLE_UPDATE, network_utils.concat_buffers(
                    network_utils.encode_int(network_id),
                    network_utils.encode_int(index),
                    serialization
                ));
                break;

            case MESSAGE.TRIGGER:
                if (args.Length != 2)
                    throw new System.Exception("Wrong number of arguments!");

                network_id = (int)args[0];
                int number = (int)args[1];
                send(MESSAGE.TRIGGER, network_utils.concat_buffers(
                    network_utils.encode_int(network_id),
                    network_utils.encode_int(number)
                ));
                break;

            case MESSAGE.KICK:
                if (args.Length != 1)
                    throw new System.Exception("Wrong number of arguments!");

                string username = (string)args[0];
                send(MESSAGE.KICK, network_utils.encode_string(username));
                break;

            default:
                throw new System.Exception("Unkown message type!");
        }
    }

    /// <summary> Parse a message of the given <paramref name="type"/> from the server that is
    /// stored between <paramref name="offset"/> and <paramref name="offset"/>+<paramref name="length"/>
    /// in <paramref name="buffer"/>. </summary>
    static void parse_message(byte type_byte, byte[] buffer, int offset, int length)
    {
        if (!System.Enum.IsDefined(typeof(server.MESSAGE), type_byte))
            throw new System.Exception(type_byte + " is not a valid message-to-client type!");

        var type = (server.MESSAGE)type_byte;

        // Setup message parsers
        switch (type)
        {
            case server.MESSAGE.CREATE:
                create_from_network(buffer, offset, length);
                break;

            case server.MESSAGE.FORCE_CREATE:

                Vector3 position = network_utils.decode_vector3(buffer, ref offset);
                string prefab = network_utils.decode_string(buffer, ref offset);
                int network_id = network_utils.decode_int(buffer, ref offset);
                int parent_id = network_utils.decode_int(buffer, ref offset);

                var created = create(position, prefab,
                    parent: parent_id > 0 ? networked.find_by_id(parent_id) : null,
                    network_id: network_id);
                break;

            case server.MESSAGE.CREATION_SUCCESS:

                // Update the local id to a network-wide one
                int local_id = network_utils.decode_int(buffer, ref offset);
                network_id = network_utils.decode_int(buffer, ref offset);
                var nw = networked.try_find_by_id(local_id);
                if (nw != null) nw.network_id = network_id;
                break;

            case server.MESSAGE.DELETE_SUCCESS:

                // A delete was succesfully processed on the server
                network_id = network_utils.decode_int(buffer, ref offset);
                networked.on_delete_success_response(network_id);
                break;

            case server.MESSAGE.VARIABLE_UPDATE:

                // Forward the variable update to the correct object
                int start = offset;
                int id = network_utils.decode_int(buffer, ref offset);
                int index = network_utils.decode_int(buffer, ref offset);
                nw = networked.try_find_by_id(id);
                nw?.variable_update(index, buffer, offset, length - (offset - start));
                break;

            case server.MESSAGE.TRIGGER:

                // Trigger a network event on a given object
                network_id = network_utils.decode_int(buffer, ref offset);
                int event_number = network_utils.decode_int(buffer, ref offset);
                nw = networked.try_find_by_id(network_id);
                nw?.on_network_event_triggered(event_number);
                break;

            case server.MESSAGE.UNLOAD:

                // Remove the object from the client
                id = network_utils.decode_int(buffer, ref offset);
                bool deleting = network_utils.decode_bool(buffer, ref offset);
                var found = networked.try_find_by_id(id);
                found?.forget(deleting);
                break;

            case server.MESSAGE.GAIN_AUTH:

                // Gain authority over a networked object
                network_id = network_utils.decode_int(buffer, ref offset);
                nw = networked.try_find_by_id(network_id);
                nw?.gain_authority();
                break;

            case server.MESSAGE.LOSE_AUTH:

                // Loose authority over a networked object
                network_id = network_utils.decode_int(buffer, ref offset);
                nw = networked.try_find_by_id(network_id);
                nw?.lose_authority();
                break;

            case server.MESSAGE.HEARTBEAT:

                int heartbeat_key = network_utils.decode_int(buffer, ref offset);
                last_server_time = network_utils.decode_int(buffer, ref offset);
                last_server_time_local = (int)Time.realtimeSinceStartup;

                if (last_heartbeat.Key == heartbeat_key)
                {
                    // Record the ping
                    last_ping = Time.realtimeSinceStartup - last_heartbeat.Value;
                }
                else
                {
                    last_ping = -1;
                    Debug.Log("Heartbeat key mismatch, packet loss/very high ping?");
                }

                heartbeat_callbacks?.Invoke();
                heartbeat_callbacks = null;
                break;

            case server.MESSAGE.DISCONNECT:

                // The server told us to disconnect. How rude.
                string msg = network_utils.decode_string(buffer, ref offset);
                disconnect(false, msg_from_server: msg);
                break;

            case server.MESSAGE.PLAYER_UPDATE:

                // Update player infos from server message
                string uname = network_utils.decode_string(buffer, ref offset);
                Vector3 pos = network_utils.decode_vector3(buffer, ref offset);
                bool con = network_utils.decode_bool(buffer, ref offset);

                player_infos[uname] = new player_info
                {
                    position = pos,
                    connected = con
                };
                break;

            case server.MESSAGE.JOINABLE_RESPONSE:

                // Leftover joinable responses might have been sent
                // when we queried if this server was joinable, just ignore them
                bool joinable = network_utils.decode_bool(buffer, ref offset);
                Debug.Log("Server joinable = " + joinable);
                break;

            default:
                throw new System.Exception("Unkown message type: " + type_byte + " (" + type + ")");
        }
    }

#endif
}
