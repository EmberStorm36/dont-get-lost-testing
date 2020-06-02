﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class networked : MonoBehaviour
{
    //###################//
    // OVERRIDABLE STUFF //
    //###################//

    /// <summary> My radius as far as the server is concerned 
    /// for determining if I can be seen. </summary>
    public virtual float network_radius() { return 1f; }

    /// <summary> How far I move before sending updated positions to the server. </summary>
    public virtual float position_resolution() { return 0.1f; }

    /// <summary> How fast I lerp my position. </summary>
    public virtual float position_lerp_speed() { return 5f; }

    /// <summary> Called when network variables should be initialized. </summary>
    public virtual void on_init_network_variables() { }

    /// <summary> Called when created (either when <see cref="client.create"/> is called,
    /// or when <see cref="client.create_from_network"/> is called). </summary>
    public virtual void on_create() { }

    /// <summary> Called the first time we're created. </summary>
    public virtual void on_first_create() { }

    /// <summary> Called when we gain authority over an object. </summary>
    public virtual void on_gain_authority() { }

    /// <summary> Called when we loose authority over an object. </summary>
    public virtual void on_loose_authority() { }

    /// <summary> Called whenever client.update() is. </summary>
    public virtual void on_network_update() { }

    /// <summary> Called whenever a networked child is added. </summary>
    public virtual void on_add_networked_child(networked child) { }

    /// <summary> Called when this object is forgotten on a client. </summary>
    public virtual void on_forget() { }

    /// <summary> Called the first time this object reccives a positive id. </summary>
    public virtual void on_first_register() { }

    //#####################//
    // NETWORKED VARIABLES //
    //#####################//

    /// <summary> Called just before we create this object. </summary>
    public void init_network_variables()
    {
        x_local = new networked_variables.net_float(
            lerp_speed: position_lerp_speed(), resolution: position_resolution());

        y_local = new networked_variables.net_float(
            lerp_speed: position_lerp_speed(), resolution: position_resolution());

        z_local = new networked_variables.net_float(
            lerp_speed: position_lerp_speed(), resolution: position_resolution());

        // The local position should be set immediately
        // to the networked value if this client has authority
        // or this is the initialization of the networked value.
        // (otherwise it should LERP to the networked
        // value; see network_update).

        x_local.on_change = () =>
        {
            if (has_authority || !x_local.initialized)
            {
                Vector3 local_pos = transform.localPosition;
                local_pos.x = x_local.value;
                transform.localPosition = local_pos;
                x_local.reset_lerp();
            }
        };

        y_local.on_change = () =>
        {
            if (has_authority || !y_local.initialized)
            {
                Vector3 local_pos = transform.localPosition;
                local_pos.y = y_local.value;
                transform.localPosition = local_pos;
                y_local.reset_lerp();
            }
        };

        z_local.on_change = () =>
        {
            if (has_authority || !z_local.initialized)
            {
                Vector3 local_pos = transform.localPosition;
                local_pos.z = z_local.value;
                transform.localPosition = local_pos;
                z_local.reset_lerp();
            }
        };

        on_init_network_variables();

        networked_variables = new List<networked_variable>();
        foreach (var f in networked_fields[GetType()])
            networked_variables.Add((networked_variable)f.GetValue(this));
    }

    /// <summary> All of the variables I contain that
    /// are serialized over the network. </summary>
    List<networked_variable> networked_variables;

    // Networked position (used by the engine to determine visibility)
    [engine_networked_variable(engine_networked_variable.TYPE.POSITION_X)]
    networked_variables.net_float x_local;

    [engine_networked_variable(engine_networked_variable.TYPE.POSITION_Y)]
    networked_variables.net_float y_local;

    [engine_networked_variable(engine_networked_variable.TYPE.POSITION_Z)]
    networked_variables.net_float z_local;

    /// <summary> Called every time client.update is called. </summary>
    public void network_update()
    {
        if (!has_authority)
        {
            // Not controlled by this => LERP my position to networked position
            // We do this first, so that transform.localPosition is properly
            // initialized before the first on_network_update() call
            transform.localPosition = new Vector3(
                x_local.lerped_value,
                y_local.lerped_value,
                z_local.lerped_value
            );
        }

        // Call implementation-specific network updates
        // We do this before sending variable updates
        // so that if networked variables change in
        // on_network_update, we send their changed
        // values immediately
        on_network_update();

        if (network_id > 0) // Registered on the network
        {
            // Send queued variable updates
            for (int i = 0; i < networked_variables.Count; ++i)
            {
                var nv = networked_variables[i];
                if (nv.queued_serial != null)
                {
                    client.send_variable_update(network_id, i, nv.queued_serial);
                    nv.queued_serial = null;
                }
            }
        }
    }

    /// <summary> My position as stored by the network. </summary>
    public Vector3 networked_position
    {
        get => new Vector3(x_local.value, y_local.value, z_local.value);
        set
        {
            transform.position = value;
            x_local.value = transform.localPosition.x;
            y_local.value = transform.localPosition.y;
            z_local.value = transform.localPosition.z;
        }
    }

    /// <summary> Serailize all of my network variables into a single byte array. </summary>
    public byte[] serialize_networked_variables()
    {
        List<byte> serial = new List<byte>();
        for (int i = 0; i < networked_variables.Count; ++i)
        {
            var nv_btyes = networked_variables[i].serialization();
            serial.AddRange(network_utils.encode_int(nv_btyes.Length));
            serial.AddRange(nv_btyes);
        }
        return serial.ToArray();
    }

    /// <summary> Called when the networked variable with the given index
    /// recives an updated serialization. </summary>
    public void variable_update(int index, byte[] buffer, int offset, int length)
    {
        networked_variables[index].reccive_serialization(buffer, offset, length);
    }

    //###############//
    // NETWORK STATE //
    //###############//

    /// <summary> My unique id on the network. Negative values are unique only on this
    /// client and indicate that I'm awating a network-wide id. </summary>
    public int network_id
    {
        get => _network_id;
        set
        {
            objects.Remove(_network_id);

            if (objects.ContainsKey(value))
                throw new System.Exception("Tried to overwrite network id!");

            int old_id = _network_id;

            objects[value] = this;
            _network_id = value;

            if (old_id <= 0 && _network_id > 0)
                on_first_register();
        }
    }
    int _network_id;

    /// <summary> Does this client have unique authority over this object. </summary>
    public bool has_authority { get; private set; }

    public void gain_authority()
    {
        has_authority = true;
        on_gain_authority();
    }

    public void lose_authority()
    {
        has_authority = false;
        on_loose_authority();
    }

    //##########################//
    // TERMINATION OF EXISTANCE //
    //##########################//

    /// <summary> Forget the netowrk object on this client. The object
    /// remains on the server + potentially on other clients. </summary>
    public void forget()
    {
        on_forget();
        network_utils.top_down<networked>(transform, (nw) => objects.Remove(nw.network_id));
        Destroy(gameObject);
    }

    /// <summary> Remove a networked object from the server and all clients. </summary>
    public void delete()
    {
        // Deactivate the object immediately, but
        // delete only once we have a positive network ID
        gameObject.SetActive(false);
        CancelInvoke("delete");

        if (network_id < 0)
        {
            // If unregistered, try again until registered.
            Invoke("delete", 0.1f);
            return;
        }

        on_forget();
        client.on_delete(this);
        network_utils.top_down<networked>(transform, (nw) => objects.Remove(nw.network_id));
        Destroy(gameObject);
    }

    //################//
    // STATIC METHODS //
    //################//

    /// <summary> Look up a networked prefab from the prefab path. </summary>
    public static networked look_up(string path)
    {
        var found = Resources.Load<networked>(path);
        if (found == null) throw new System.Exception("Could not find the prefab " + path);
        return found;
    }

    /// <summary> Contains all of the networked fields, keyed by networked type. </summary>
    static Dictionary<System.Type, List<System.Reflection.FieldInfo>> networked_fields;

    /// <summary> Load the <see cref="networked_fields"/> dictionary. </summary>
    public static void load_networked_fields()
    {
        networked_fields = new Dictionary<System.Type, List<System.Reflection.FieldInfo>>();

        // Loop over all networked implementations
        foreach (var type in typeof(networked).Assembly.GetTypes())
        {
            if (type.IsAbstract) continue;
            if (!type.IsSubclassOf(typeof(networked))) continue;

            var fields = new List<System.Reflection.FieldInfo>();
            int special_fields_count = System.Enum.GetNames(typeof(engine_networked_variable.TYPE)).Length;
            var special_fields = new System.Reflection.FieldInfo[special_fields_count];

            // Find all networked_variables in this type (or it's base types)
            for (var t = type; t != typeof(networked).BaseType; t = t.BaseType)
            {
                foreach (var f in t.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic))
                {
                    var ft = f.FieldType;
                    if (ft.IsAbstract) continue;
                    if (!ft.IsSubclassOf(typeof(networked_variable))) continue;

                    // Find special fields that are used by the engine
                    var attrs = f.GetCustomAttributes(typeof(engine_networked_variable), true);
                    if (attrs.Length != 0)
                    {
                        if (attrs.Length > 1)
                        {
                            string err = "More than one " + typeof(engine_networked_variable) +
                                         " found on a field!";
                            throw new System.Exception(err);
                        }

                        var atr = (engine_networked_variable)attrs[0];
                        special_fields[(int)atr.type] = f;
                        continue;
                    }

                    fields.Add(f);
                }
            }

            // Check we've got all of the special fields
            for (int i = 0; i < special_fields.Length; ++i)
                if (special_fields[i] == null)
                    throw new System.Exception("Engine field not found for type" + type + "!");

            // Sort fields alphabetically, so the order 
            // is the same on every client.
            fields.Sort((f1, f2) => f1.Name.CompareTo(f2.Name));

            // Add the special fields at the start, so we
            // know how to access them in the engine.
            fields.InsertRange(0, special_fields);

            networked_fields[type] = fields;
        }
    }

    /// <summary> The objects on this client, keyed by their network id. </summary>
    static Dictionary<int, networked> objects = new Dictionary<int, networked>();

    /// <summary> Return the object with the given network id. 
    /// Throws an error if it doesn't exist. </summary>
    public static networked find_by_id(int id)
    {
        networked found;
        if (!objects.TryGetValue(id, out found))
            throw new System.Exception("Could not find object with network id " + id);
        return found;
    }

    /// <summary> Attempt to find a networked object.
    /// Returns null if it doesn't exist. </summary>
    public static networked try_find_by_id(int id)
    {
        networked found;
        if (!objects.TryGetValue(id, out found))
            return null;
        return found;
    }

    /// <summary> Called every time client.update is called. </summary>
    public static void network_updates()
    {
        foreach (var kv in objects)
            kv.Value.network_update();
    }

    public string network_info()
    {
        return "Network id = " + network_id + "\n" +
               "Has authority = " + has_authority;
    }

    public static int object_count { get => objects.Count; }

#if UNITY_EDITOR

    // The custom editor for networked types
    [UnityEditor.CustomEditor(typeof(networked), true)]
    public class editor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            if (Application.isPlaying)
            {
                var nw = (networked)target;
                UnityEditor.EditorGUILayout.TextArea("Network info\n" + nw.network_info());
            }
        }
    }

#endif
}

public class networked_player : networked
{
    /// <summary> How far can the player see other networked objects? </summary>
    public float render_range
    {
        get => _render_range;
        set
        {
            if (_render_range == value)
                return; // No change

            _render_range = value;
            client.on_render_range_change(this);
        }
    }
    float _render_range = server.INIT_RENDER_RANGE;
}