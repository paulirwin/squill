-- Given an inventory item, returns the customer_id currently holding it (an open rental with
-- no return_date), or NULL if the item is on the shelf. Used by inventory_in_stock. Declared
-- as plpgsql because it uses a control-flow IF over a query result.
CREATE FUNCTION inventory_held_by_customer(p_inventory_id integer)
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    v_customer_id integer;
BEGIN
    SELECT customer_id INTO v_customer_id
    FROM rental
    WHERE return_date IS NULL
      AND inventory_id = p_inventory_id;

    RETURN v_customer_id;
END
$$;
