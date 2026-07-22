CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    name  varchar(100) NOT NULL,
    price numeric(6, 2) NOT NULL
);

-- A SQL-language function that queries a table declared in the same project — it only
-- deploys cleanly if functions are created after the tables they reference.
CREATE FUNCTION widget_count() RETURNS bigint
LANGUAGE sql
STABLE
AS $$
SELECT count(*) FROM widgets
$$;

-- A SETOF function with an OUT parameter, an IMMUTABLE + STRICT SQL function, covering the
-- volatility/strictness facets.
CREATE FUNCTION cheap_widget_ids(max_price numeric, OUT id integer)
RETURNS SETOF integer
LANGUAGE sql
STABLE
AS $$
SELECT id FROM widgets WHERE price <= max_price
$$;

CREATE FUNCTION add_tax(amount numeric, rate numeric) RETURNS numeric
LANGUAGE sql
IMMUTABLE STRICT
AS $$
SELECT amount * (1 + rate)
$$;

-- A plpgsql function with a control-flow body, SECURITY DEFINER.
CREATE FUNCTION widget_name(p_id integer) RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_name text;
BEGIN
    SELECT name INTO v_name FROM widgets WHERE id = p_id;
    RETURN v_name;
END
$$;

-- An overload of add_tax: same name, different argument types, so it must round-trip as a
-- distinct object.
CREATE FUNCTION add_tax(amount integer) RETURNS integer
LANGUAGE sql
IMMUTABLE
AS $$
SELECT amount + 1
$$;
