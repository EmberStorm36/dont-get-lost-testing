﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class item_source : item_proccessor
{
    public item item;
    public float time_between_items = 1f;

    float last_create_time = 0;

    private void Update()
    {
        if (Time.time > last_create_time + time_between_items)
        {
            last_create_time = Time.time;
            create();
        }
    }

    int items_created = 0;
    void create()
    {
        var output_link = link_points[items_created % link_points.Length];
        if (output_link.type != item_link_point.TYPE.OUTPUT)
            throw new System.Exception("Item source link is not marked as output!");

        if (output_link.linked_to == null)
            return; // Don't spawn anything unless we're linked up

        if (output_link.item != null)
            return; // Output backed up, don't spawn anything

        // Spawn the new item
        var spawned = item.inst();
        spawned.transform.position = output_link.position;
        output_link.item = spawned;
        ++items_created;
    }
}
