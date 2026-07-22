-- The Sakila `inventory_in_stock` stored function. Returns TRUE when an inventory item is
-- available to rent (either it has never been rented, or its most recent rental has been
-- returned). Exercises a FUNCTION returning `boolean`, `COUNT(*) INTO` a variable, and IF/ELSE
-- control flow — and is itself called from the `film_in_stock` / `film_not_in_stock`
-- procedures, demonstrating routine-to-routine dependencies.
CREATE FUNCTION inventory_in_stock(p_inventory_id int)
    RETURNS boolean
    READS SQL DATA
BEGIN
    DECLARE v_rentals INT;
    DECLARE v_out INT;

    -- AN ITEM IS IN-STOCK IF THERE ARE EITHER NO ROWS IN THE rental TABLE
    -- FOR THE ITEM OR ALL ROWS HAVE return_date POPULATED

    SELECT COUNT(*) INTO v_rentals
    FROM rental
    WHERE inventory_id = p_inventory_id;

    IF v_rentals = 0 THEN
        RETURN TRUE;
    END IF;

    SELECT COUNT(rental_id) INTO v_out
    FROM inventory
        LEFT JOIN rental USING (inventory_id)
    WHERE inventory.inventory_id = p_inventory_id
      AND rental.return_date IS NULL;

    IF v_out > 0 THEN
        RETURN FALSE;
    ELSE
        RETURN TRUE;
    END IF;
END;
