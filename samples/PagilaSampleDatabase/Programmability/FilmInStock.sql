-- Returns the set of inventory_ids for a given film at a given store that are currently in
-- stock (available to rent). Declared RETURNS SETOF integer, so it yields a result set rather
-- than a scalar. The p_film_count OUT parameter is unused by callers but preserved from the
-- canonical Sakila signature.
CREATE FUNCTION film_in_stock(p_film_id integer, p_store_id integer, OUT p_film_count integer)
RETURNS SETOF integer
LANGUAGE sql
AS $$
SELECT inventory_id
FROM inventory
WHERE film_id = $1
  AND store_id = $2
  AND inventory_in_stock(inventory_id)
$$;
