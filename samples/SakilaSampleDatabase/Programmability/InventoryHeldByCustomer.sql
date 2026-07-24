-- The Sakila `inventory_held_by_customer` stored function. Given an inventory item, returns
-- the customer currently holding it (an open rental with no return_date), or NULL if it is not
-- out. Exercises a FUNCTION returning an integer, a local variable, and `SELECT ... INTO` with
-- an `IS NULL` predicate, plus a bare `RETURN` as a NOT FOUND handler action.
CREATE FUNCTION inventory_held_by_customer(p_inventory_id int)
    RETURNS int
    READS SQL DATA
BEGIN
    DECLARE v_customer_id INT;
    DECLARE EXIT HANDLER FOR NOT FOUND RETURN NULL;

    SELECT customer_id INTO v_customer_id
    FROM rental
    WHERE return_date IS NULL
      AND inventory_id = p_inventory_id;

    RETURN v_customer_id;
END;
