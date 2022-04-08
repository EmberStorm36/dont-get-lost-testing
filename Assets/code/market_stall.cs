using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class market_stall : walk_to_settler_interactable
{
    public town_path_element shopkeeper_path_element;

    chest storage;
    item_input[] inputs;
    item_output output;

    protected override void on_fail_assign(settler s, ASSIGN_FAILURE_MODE failure)
    {
        Debug.Log("Failed to assign to market stall: " + failure);
    }

    public override town_path_element path_element(int group = -1)
    {
        if (shopkeeper_path_element.group == group)
            return shopkeeper_path_element;
        return null;
    }

    protected override void Start()
    {
        base.Start();

        storage = GetComponent<chest>();
        inputs = GetComponentsInChildren<item_input>();
        output = GetComponentInChildren<item_output>();

        foreach (var i in inputs)
        {
            var input = i;
            input.add_on_change_listener(() =>
            {
                var item = input.release_next_item();
                while (item != null)
                {
                    if (storage.inventory.add(item, 1))
                        Destroy(item.gameObject);
                    else
                        item_rejector.create(item);

                    item = input.release_next_item();
                }
            });
        }
    }

    item item_selling;
    float timer = 0;
    int coins_to_dispense = 0;

    enum STATE
    {
        OBTAIN_ITEM_TO_SELL,
        AWAIT_BUYER,
        SELL_ITEM,
        OUTPUT_COIN,
        STATE_COUNT
    }

    STATE state = STATE.OBTAIN_ITEM_TO_SELL;

    STATE parse_state(int stage, out bool success)
    {
        int state_number = stage % (int)STATE.STATE_COUNT;

        if (!System.Enum.IsDefined(typeof(STATE), state_number))
        {
            Debug.LogError("Unkown state number: " + state_number);
            success = false;
            return STATE.STATE_COUNT;
        }

        success = true;
        return (STATE)state_number;
    }

    public override string task_summary()
    {
        switch (state)
        {
            case STATE.OBTAIN_ITEM_TO_SELL:
                return "Running a market stall (getting stock)";

            case STATE.AWAIT_BUYER:
                return "Running a market stall (waiting for a buyer)";

            case STATE.SELL_ITEM:
                return "Running a market stall (haggling for a price)";

            case STATE.OUTPUT_COIN:
                return "Running a market stall (collecting coins)";

            default:
                return "Running a market stall";
        }
    }

    protected override STAGE_RESULT on_interact_arrived(settler s, int stage)
    {
        state = parse_state(stage, out bool success);
        if (!success)
            return STAGE_RESULT.TASK_FAILED;

        switch (state)
        {
            case STATE.OBTAIN_ITEM_TO_SELL:
                item_selling = storage.inventory.remove_first();
                if (item_selling == null)
                    return STAGE_RESULT.TASK_COMPLETE; // Sold out
                return STAGE_RESULT.STAGE_COMPLETE;

            case STATE.AWAIT_BUYER:
                timer = 0;
                return STAGE_RESULT.STAGE_COMPLETE;

            case STATE.SELL_ITEM:
                if (item_selling == null)
                {
                    Debug.LogError("Forgot what I was selling!");
                    return STAGE_RESULT.TASK_FAILED;
                }

                timer += Time.deltaTime;
                if (timer > 1)
                {
                    // Finished selling
                    timer = 0;
                    coins_to_dispense = item_selling.value;
                    return STAGE_RESULT.STAGE_COMPLETE;
                }
                else
                    return STAGE_RESULT.STAGE_UNDERWAY;

            case STATE.OUTPUT_COIN:

                if (coins_to_dispense <= 0)
                    return STAGE_RESULT.STAGE_COMPLETE;

                timer += Time.deltaTime;
                if (timer > 0.1f)
                {
                    // Dispense the next coin
                    coins_to_dispense -= 1;
                    output.add(Resources.Load<item>("items/coin"), 1);
                }

                return STAGE_RESULT.STAGE_UNDERWAY;


            default:
                Debug.LogError("Unkown state: " + stage);
                return STAGE_RESULT.STAGE_COMPLETE;
        }
    }
}
