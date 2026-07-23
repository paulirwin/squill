-- The Sakila `inventory_held_by_customer` stored function. Given an inventory item, returns
-- the customer currently holding it (an open rental with no return_date), or NULL if it is not
-- out. Exercises a FUNCTION returning an integer, a local variable, and `SELECT ... INTO` with
-- an `IS NULL` predicate.
--
-- The NOT FOUND handler wraps its RETURN in a BEGIN ... END block: the handler action is
-- written as a compound statement rather than the bare `DECLARE EXIT HANDLER FOR NOT FOUND
-- RETURN NULL;` of the canonical schema. The two are equivalent on both engines, but the
-- grammars-v4 MariaDB grammar Squill's parser is generated from does not accept a bare RETURN
-- as a handler action; the compound form is the portable spelling.
CREATE FUNCTION inventory_held_by_customer(p_inventory_id int)
    RETURNS int
    READS SQL DATA
BEGIN
    DECLARE v_customer_id INT;
    DECLARE EXIT HANDLER FOR NOT FOUND BEGIN RETURN NULL; END;

    SELECT customer_id INTO v_customer_id
    FROM rental
    WHERE return_date IS NULL
      AND inventory_id = p_inventory_id;

    RETURN v_customer_id;
END;
