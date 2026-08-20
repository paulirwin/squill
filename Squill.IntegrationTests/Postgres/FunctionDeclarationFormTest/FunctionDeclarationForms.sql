CREATE TABLE orders
(
    id       integer PRIMARY KEY,
    customer text    NOT NULL,
    total    numeric(10, 2) NOT NULL
);

-- RETURNS TABLE with several columns. Measured on postgres:18.4: PostgreSQL stores the
-- columns as TABLE-mode arguments (proargmodes 't') with proretset set and prorettype
-- `record`, so a multi-column form only round-trips if the columns are modeled as arguments.
CREATE FUNCTION order_summary(p_customer text)
RETURNS TABLE (id integer, total numeric)
LANGUAGE sql
STABLE
AS $$
SELECT id, total FROM orders WHERE customer = p_customer
$$;

-- A single-column RETURNS TABLE, whose prorettype is that column's type rather than
-- `record` — the boundary case that would break a model assuming `record` throughout.
CREATE FUNCTION order_ids()
RETURNS TABLE (id integer)
LANGUAGE sql
STABLE
AS $$
SELECT id FROM orders
$$;

-- A function with no RETURNS clause: the OUT parameters define the result. One OUT
-- parameter reports prorettype as that parameter's own type.
CREATE FUNCTION order_count(OUT total bigint)
LANGUAGE sql
STABLE
AS $$
SELECT count(*) FROM orders
$$;

-- Two OUT parameters instead report `record`.
CREATE FUNCTION order_totals(OUT smallest numeric, OUT largest numeric)
LANGUAGE sql
STABLE
AS $$
SELECT min(total), max(total) FROM orders
$$;

-- The SECURITY DEFINER hardening idiom from the PostgreSQL documentation. The list value
-- must be re-emitted as individually quoted items: measured, `SET search_path TO
-- 'pg_catalog', 'pg_temp'` stores `search_path=pg_catalog, pg_temp`, while quoting the whole
-- list as one string stores a different, single-element value that would re-diff.
CREATE FUNCTION hardened_total()
RETURNS numeric
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
SELECT coalesce(sum(total), 0) FROM public.orders
$$;

-- Several SET clauses, including a scalar GUC, to prove declaration order survives.
CREATE FUNCTION tuned_count()
RETURNS bigint
LANGUAGE sql
SET enable_seqscan = off
SET work_mem = '32MB'
AS $$
SELECT count(*) FROM orders
$$;

-- A procedure carrying the same hardening idiom, since a procedure stores proconfig too.
CREATE PROCEDURE hardened_purge()
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DELETE FROM public.orders WHERE total < 0
$$;
