﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface INonBlueprintable { }

public interface IBuildListener
{
    void on_first_built();
}

public class building_material : item, IPlayerInteractable
{
    public const float BUILD_RANGE = 5f;
    public float axes_scale = 1f;

    static building_material()
    {
        tips.add("When placing an object, you can cycle through the " +
            "different starting orientations by pressing " +
            controls.bind_name(controls.BIND.INCREMENT_PIVOT) + " or by scrolling. " +
            "This initial orientation will be saved when placing subsequent objects.");

        tips.add("With a building material equipped, right click" +
            " to quickly delete objects of the same type.");

        tips.add("Building materials can be deleted by right-clicking on them with an empty hand. " +
            "Press " + controls.bind_name(controls.BIND.QUICKBAR_1) +
            " a few times to de-equip what you are holding.");

        tips.add("To turn off snapping when placing a building, hold " +
            controls.bind_name(controls.BIND.IGNORE_SNAP_POINTS) + ". " +
            "This will also orient the building to the world " +
            "axes, rather than to the parent building.");
    }

    //############//
    // NETWORKING //
    //############//

    public override void on_first_create()
    {
        base.on_first_create();
        foreach (var bl in GetComponentsInChildren<IBuildListener>())
            bl.on_first_built();
    }

    //#####################//
    // IPlayerInteractable //
    //#####################//

    class pick_up_from_gutter : player_interaction
    {
        building_material material;
        public pick_up_from_gutter(building_material material) { this.material = material; }

        public override controls.BIND keybind => controls.BIND.USE_ITEM;
        public override bool is_possible()
        {
            return material.is_logistics_version;
        }

        public override bool start_interaction(player player)
        {
            material.pick_up();
            return true;
        }

        public override string context_tip()
        {
            return "pick up " + material.display_name;
        }
    }

    class deconstruct_interaction : player_interaction
    {
        building_material material;
        public deconstruct_interaction(building_material material) { this.material = material; }

        public override controls.BIND keybind => controls.BIND.ALT_USE_ITEM;
        public override bool is_possible() { return !material.is_logistics_version; }

        public override bool start_interaction(player player)
        {
            material.pick_up(true);
            player.current.play_sound("sounds/hammer_wood_lowpass", 0.9f, 1.1f, 0.5f, location: material.transform.position);
            return true;
        }

        public override string context_tip()
        {
            return "demolish " + material.display_name;
        }
    }

    public override player_interaction[] player_interactions()
    {
        if (is_logistics_version)
            return base.player_interactions();

        return new player_interaction[]
        {
            new pick_up_from_gutter(this),
            new deconstruct_interaction(this),
            new select_matching_interaction(this),
            new player_inspectable(transform)
            {
                text = () => display_name + " (built)",
                sprite = () => sprite
            }
        };
    }

    //##########//
    // ITEM USE //
    //##########//

    // Points that welds snap to
    protected snap_point[] snap_points => GetComponentsInChildren<snap_point>();

    player_interaction[] interactions;
    public override player_interaction[] item_uses()
    {
        if (interactions == null)
            interactions = new player_interaction[]
            {
                new build_interaction(this),
                new demolish_interaction(this)
            };
        return interactions;
    }

    public bool is_blueprint => GetComponentInParent<blueprint>() != null;

    snap_point closest_snap_point(Ray ray, float ray_distance)
    {
        snap_point ret = null;

        // Attempt to raycast to this item/find the nearest
        // snap_point to the raycast hit
        RaycastHit hit;
        if (utils.raycast_for_closest<item>(
            ray, out hit, ray_distance,
            (h, t) => t == this))
        {
            // Find the nearest snap point to the hit
            float min_dis_pt = float.MaxValue;
            foreach (var s in snap_points)
            {
                float dis_pt = (s.transform.position - hit.point).sqrMagnitude;
                if (dis_pt < min_dis_pt)
                {
                    min_dis_pt = dis_pt;
                    ret = s;
                }
            }
        }

        if (ret != null)
            return ret;

        // Just find the nearest snap point to the ray
        float min_dis = float.MaxValue;
        foreach (var sp in snap_points)
        {
            Vector3 to_line = sp.transform.position - ray.origin;
            to_line -= Vector3.Project(to_line, ray.direction);
            float dis = to_line.sqrMagnitude;
            if (dis < min_dis)
            {
                min_dis = dis;
                ret = sp;
            }
        }

        return ret;
    }

    public override void on_equip(player player)
    {
        base.on_equip(player);

        if (player.has_authority)
        {
            // Draw links if a path element is equipped
            if (GetComponentInChildren<town_path_element>() != null)
                town_path_element.draw_links = true;
        }
    }

    public override void on_unequip(player player)
    {
        base.on_unequip(player);

        if (player.has_authority)
        {
            // Stop drawing debug-type things
            town_path_element.draw_links = false;
            item_node.display_enabled = false;
        }
    }

    class demolish_interaction : player_interaction
    {
        public static float last_time_deleting { get; private set; }

        building_material equipped;
        public demolish_interaction(building_material equipped) { this.equipped = equipped; }

        public override controls.BIND keybind => controls.BIND.ALT_USE_ITEM;
        public override bool allow_held => true;

        public override string context_tip() => "destroy matching objects";

        public override bool continue_interaction(player player)
        {
            // Stop cancelling a build with right-click from immediately 
            // destroying the object we were welding to during the build.
            const float TIME_BETWEEN_DELETES = 0.1f;
            if (Time.realtimeSinceStartup < last_time_deleting + TIME_BETWEEN_DELETES ||
                Time.realtimeSinceStartup < build_interaction.last_time_building + TIME_BETWEEN_DELETES)
                return true;

            // Right click destroys items of the same kind
            var camera_ray = player.camera_ray(BUILD_RANGE, out float dis);
            building_material found_same = utils.raycast_for_closest<building_material>(
                camera_ray, out RaycastHit hit, dis, (h, b) => b.name == equipped.name);
            if (found_same == null) return true;

            last_time_deleting = Time.realtimeSinceStartup;
            found_same.pick_up(register_undo: true);
            player.current.play_sound("sounds/hammer_wood_lowpass", 0.9f, 1.1f, 0.5f, location: hit.point);
            return true;
        }
    }

    class build_interaction : player_interaction
    {
        public static float last_time_building { get; private set; }

        blueprint blueprint;
        building_material equipped;

        public build_interaction(building_material equipped) { this.equipped = equipped; }
        public override controls.BIND keybind => controls.BIND.USE_ITEM;

        public override bool allows_movement()
        {
            // Disable movement when using keys to manipulate
            return !controls.key_based_building;
        }

        public override string context_tip()
        {
            if (blueprint != null)
            {
                if (controls.key_based_building)
                {
                    return "build, " + controls.bind_name(controls.BIND.ALT_USE_ITEM) + "to cancel \nUse " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_FORWARD) + ", " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_LEFT) + ", " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_BACK) + ", " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_RIGHT) + ", " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_DOWN) + " and " +
                        controls.bind_name(controls.BIND.MANIPULATE_BUILDING_UP) + " to rotate the building\n" +
                        "Hold " + controls.bind_name(controls.BIND.BUILDING_TRANSLATION) + " to translate instead\n" +
                        "Scroll, or press " + controls.bind_name(controls.BIND.INCREMENT_PIVOT) +
                        " to cycle initial orientations (purple spheres)\n" +
                        "Hold " + controls.bind_name(controls.BIND.FINE_ROTATION) + " to disable rotation snapping";
                }
                else
                {
                    return
                        "Double tap " + controls.bind_name(keybind) + " to build, " + controls.bind_name(controls.BIND.ALT_USE_ITEM) + " to cancel\n" +
                        "Click and drag the arrows to translate, or the circles to rotate\n" +
                        "Scroll, or press " + controls.bind_name(controls.BIND.INCREMENT_PIVOT) + " to cycle initial orientations (purple spheres)\n" +
                        "Hold " + controls.bind_name(controls.BIND.FINE_ROTATION) + " to disable rotation snapping";
                }
            }

            return "build\n" +
                "Buildings will snap together at key points, hold " +
                controls.bind_name(controls.BIND.IGNORE_SNAP_POINTS) + " to disable this\n" +
                "Disabling snapping will also allign the building to the world axes";
        }

        public override bool start_interaction(player player)
        {
            // Don't do anything on non-auth clients
            if (!player.has_authority) return true;

            if (equipped.snap_points.Length == 0)
            {
                Debug.LogError("No snap points found on " + equipped.display_name + "!");
                return true;
            }

            // Get the ray to cast along, that stays within 
            // BUILD_RANGE of the player
            float raycast_distance = 0;
            var camera_ray = player.current.camera_ray(BUILD_RANGE, out raycast_distance);

            // Find a (non-logistics version) building_material/snap_point 
            // under cursor (unless ignore_snap_points is held)
            RaycastHit hit = default;
            building_material bm = null;
            if (!controls.held(controls.BIND.IGNORE_SNAP_POINTS))
                bm = utils.raycast_for_closest<building_material>(
                    camera_ray, out hit, raycast_distance, accept: (h, b) => !b.is_logistics_version);

            // If a building material is found, fix new build to it
            // otherwise, just fix to any solid object
            if (bm != null)
            {
                var snap = bm.closest_snap_point(camera_ray, raycast_distance);

                // Get the rotation that is maximally alligned with the player
                // but still snapped to the snap point axes
                var alligned_rot = Quaternion.LookRotation(
                    utils.find_to_min(snap.snap_directions_45(), (a) => -Vector3.Dot(a, player.transform.forward)),
                    utils.find_to_min(snap.snap_directions_45(), (a) => -Vector3.Dot(a, player.transform.up))
                    );

                blueprint = blueprint.create(equipped.name, snap.transform.position, alligned_rot);
            }
            else
            {
                var col = utils.raycast_for_closest<Collider>(camera_ray, out hit, raycast_distance,
                    (h, c) => !c.transform.IsChildOf(player.current.transform));

                if (col != null)
                    blueprint = blueprint.create(equipped.name, hit.point, player.current.transform.rotation);
            }

            // Move onto rotation stage if something was spawned
            if (blueprint != null)
            {
                player.current.play_sound("sounds/hammer_wood_lowpass", 0.9f, 1.1f, 0.5f, location: hit.point);
                if (!controls.key_based_building) player.current.cursor_sprite = cursors.DEFAULT;
                return false;
            }

            return true;
        }

        public override bool continue_interaction(player player)
        {
            // Don't do anything on non-auth clients
            if (!player.has_authority) return true;
            if (blueprint == null) return true;

            last_time_building = Time.realtimeSinceStartup;

            if (controls.triggered(controls.BIND.ALT_USE_ITEM))
            {
                // Cancel build on right click
                Destroy(blueprint.gameObject);
                blueprint = null;
                return true;
            }

            return blueprint.manipulate();
        }

        public override void end_interaction(player player)
        {
            // Don't do anything non-auth clients
            if (!player.has_authority) return;

            if (blueprint != null)
            {
                // Remove the object we're building from the inventory
                if (player.current.inventory.remove(blueprint.building, 1))
                {
                    player.current.play_sound("sounds/hammer_wood", 0.9f, 1.1f, 0.5f,
                        location: blueprint.transform.position);

                    // Create a proper, networked version of the spawned object
                    var b = blueprint.building;
                    var created = create(b.name, b.transform.position, b.transform.rotation,
                        networked: true, register_undo: true) as building_material;
                    created?.on_build();
                    Destroy(blueprint.gameObject);

                    // Satisfy building requirements
                    build_requirement.on_build(created);
                }
            }

            blueprint = null;
            player.current.validate_equip();
        }
    }

    /// <summary> Called when the object is built. </summary>
    protected virtual void on_build() { }
}

public abstract class building_with_inventory : building_material
{
    public inventory inventory { get; private set; }

    protected override bool can_pick_up(out string message)
    {
        if (inventory != null && !inventory.empty)
        {
            message = "Inventory not empty!";
            return false;
        }

        return base.can_pick_up(out message);
    }

    protected abstract string inventory_prefab();
    protected virtual void on_set_inventory() { }

    public override void on_first_register()
    {
        base.on_first_register();
        client.create(transform.position, inventory_prefab(), this);
    }

    public override void on_add_networked_child(networked child)
    {
        base.on_add_networked_child(child);
        if (child is inventory)
        {
            inventory = (inventory)child;
            on_set_inventory();
        }
    }
}