-- The complement of film_in_stock: the inventory_ids for a film at a store that are currently
-- rented out (not in stock). Also RETURNS SETOF integer.
CREATE FUNCTION film_not_in_stock(p_film_id integer, p_store_id integer, OUT p_film_count integer)
RETURNS SETOF integer
LANGUAGE sql
AS $$
SELECT inventory_id
FROM inventory
WHERE film_id = $1
  AND store_id = $2
  AND NOT inventory_in_stock(inventory_id)
$$;
