-- Returns true if a given inventory item is available to rent. An item is in stock if it has
-- never been rented, or if all of its rentals have been returned. plpgsql with a boolean
-- result.
CREATE FUNCTION inventory_in_stock(p_inventory_id integer)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    v_rentals integer;
    v_out     integer;
BEGIN
    -- Has the item ever been rented?
    SELECT count(*) INTO v_rentals
    FROM rental
    WHERE inventory_id = p_inventory_id;

    IF v_rentals = 0 THEN
        RETURN true;
    END IF;

    -- Of those rentals, how many are still out (not yet returned)?
    SELECT count(rental_id) INTO v_out
    FROM inventory
        LEFT JOIN rental USING (inventory_id)
    WHERE inventory.inventory_id = p_inventory_id
      AND rental.return_date IS NULL;

    IF v_out > 0 THEN
        RETURN false;
    ELSE
        RETURN true;
    END IF;
END
$$;
