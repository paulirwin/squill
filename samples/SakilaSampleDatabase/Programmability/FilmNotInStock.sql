-- The Sakila `film_not_in_stock` stored procedure — the counterpart to `film_in_stock`.
-- Returns the inventory IDs of a film that are NOT currently in stock at a given store, using
-- a NOT IN subquery against the `rental` table, and reports the count through an OUT parameter.
CREATE PROCEDURE film_not_in_stock(IN p_film_id int, IN p_store_id int, OUT p_film_count int)
    READS SQL DATA
BEGIN
    SELECT inventory_id
    FROM inventory
    WHERE film_id = p_film_id
      AND store_id = p_store_id
      AND NOT inventory_in_stock(inventory_id);

    SELECT COUNT(*)
    FROM inventory
    WHERE film_id = p_film_id
      AND store_id = p_store_id
      AND NOT inventory_in_stock(inventory_id)
    INTO p_film_count;
END;
