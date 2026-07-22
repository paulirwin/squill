-- The Sakila `film_in_stock` stored procedure. Returns the inventory IDs of a film that are
-- currently in stock at a given store, and reports the count through an OUT parameter.
-- Exercises IN and OUT parameters together, a result-set-returning SELECT, and
-- `SELECT FOUND_ROWS()` to obtain the row count.
CREATE PROCEDURE film_in_stock(IN p_film_id int, IN p_store_id int, OUT p_film_count int)
    READS SQL DATA
BEGIN
    SELECT inventory_id
    FROM inventory
    WHERE film_id = p_film_id
      AND store_id = p_store_id
      AND inventory_in_stock(inventory_id);

    SELECT COUNT(*)
    FROM inventory
    WHERE film_id = p_film_id
      AND store_id = p_store_id
      AND inventory_in_stock(inventory_id)
    INTO p_film_count;
END;
