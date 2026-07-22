-- A user-defined ENUM type. PostgreSQL treats an enum as a first-class, ordered type whose
-- allowed values are fixed at declaration time. film.rating is typed as this enum below, so
-- the type is deployed before any table that references it.
CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');

-- A DOMAIN: a base type (integer) constrained by a named CHECK. film.release_year is typed
-- as this domain, so every value stored there is validated against the range without having
-- to repeat the constraint on each column. The domain is deployed before the tables that use
-- it.
CREATE DOMAIN year AS integer
    CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);

-- Lookup table of countries, referenced by city. Top of the geographic hierarchy
-- (country -> city -> address), so it is deployed before the tables that reference it.
CREATE TABLE country
(
    country_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    country     varchar(50) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

-- Cities, each belonging to a country. The inline REFERENCES declares the foreign key back
-- to country; the matching index below backs that key so lookups by country_id stay fast.
CREATE TABLE city
(
    city_id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    city        varchar(50) NOT NULL,
    country_id  integer NOT NULL REFERENCES country (country_id),
    last_update timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_fk_country_id ON city (country_id);

-- Street addresses, each tied to a city. Nullable columns (address2, postal_code) model
-- optional address parts; the inline REFERENCES declares the foreign key to city.
CREATE TABLE address
(
    address_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    address     varchar(50) NOT NULL,
    address2    varchar(50),
    district    varchar(20) NOT NULL,
    city_id     integer NOT NULL REFERENCES city (city_id),
    postal_code varchar(10),
    phone       varchar(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_fk_city_id ON address (city_id);

-- Languages a film can be recorded in. Note name is a fixed-width char(20) (blank-padded),
-- matching the canonical Sakila schema rather than a varchar.
CREATE TABLE language
(
    language_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        char(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

-- Film categories (genres), joined to films through film_category.
CREATE TABLE category
(
    category_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        varchar(25) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

-- The actors that appear in films. A surrogate identity key stands in for the natural key,
-- and last_update is stamped on every row change by the last_updated trigger (see Triggers/).
CREATE TABLE actor
(
    actor_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_actor_last_name ON actor (last_name);

-- The catalogue of films. This is the richest table in the schema and exercises several
-- advanced PostgreSQL features:
--   * release_year is typed as the "year" DOMAIN (see Types/Year.sql), so its range check
--     travels with the type.
--   * rating is the "mpaa_rating" ENUM (see Types/MpaaRating.sql), defaulting to 'G'.
--   * special_features is a text ARRAY (text[]).
--   * fulltext is a tsvector, kept current by the film_fulltext_trigger and searched through
--     the GiST index below.
-- Two self-referential-style foreign keys point at language: the spoken language and the
-- optional original language.
CREATE TABLE film
(
    film_id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title                varchar(255) NOT NULL,
    description          text,
    release_year         year,
    language_id          integer NOT NULL REFERENCES language (language_id),
    original_language_id integer REFERENCES language (language_id),
    rental_duration      smallint NOT NULL DEFAULT 3,
    rental_rate          numeric(4,2) NOT NULL DEFAULT 4.99,
    length               smallint,
    replacement_cost     numeric(5,2) NOT NULL DEFAULT 19.99,
    rating               mpaa_rating DEFAULT 'G',
    last_update          timestamp NOT NULL DEFAULT now(),
    special_features     text[],
    fulltext             tsvector NOT NULL
);

CREATE INDEX idx_title ON film (title);
CREATE INDEX idx_fk_language_id ON film (language_id);
CREATE INDEX idx_fk_original_language_id ON film (original_language_id);

-- A GiST index over the tsvector column enables fast full-text search on the film's fulltext.
CREATE INDEX film_fulltext_idx ON film USING gist (fulltext);

-- Junction table resolving the many-to-many relationship between films and actors. The
-- primary key is the composite (actor_id, film_id), declared as a table-level PRIMARY KEY
-- since it spans more than one column. An extra index on film_id backs the reverse lookup.
CREATE TABLE film_actor
(
    actor_id    integer NOT NULL REFERENCES actor (actor_id),
    film_id     integer NOT NULL REFERENCES film (film_id),
    last_update timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (actor_id, film_id)
);

CREATE INDEX idx_fk_film_id ON film_actor (film_id);

-- Junction table linking films to categories, with a composite (film_id, category_id)
-- primary key declared at table level.
CREATE TABLE film_category
(
    film_id     integer NOT NULL REFERENCES film (film_id),
    category_id integer NOT NULL REFERENCES category (category_id),
    last_update timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (film_id, category_id)
);

-- A rental store. store and staff form a circular foreign-key pair: store.manager_staff_id
-- references staff, while staff.store_id references store. To keep the schema declarative we
-- express the store -> staff direction as an inline REFERENCES here; because staff is created
-- after store, the reference resolves once both tables exist. A unique index enforces that a
-- given staff member manages at most one store.
CREATE TABLE store
(
    store_id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    manager_staff_id integer NOT NULL REFERENCES staff (staff_id),
    address_id       integer NOT NULL REFERENCES address (address_id),
    last_update      timestamp NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX idx_unq_manager_staff_id ON store (manager_staff_id);

-- Store employees. staff.store_id references store, and store.manager_staff_id references
-- staff back the other way (see Store.sql) — the two tables form a circular FK pair that
-- Squill deploys by creating both tables before resolving the references between them. The
-- picture column stores a small image inline as bytea.
CREATE TABLE staff
(
    staff_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    address_id  integer NOT NULL REFERENCES address (address_id),
    email       varchar(50),
    store_id    integer NOT NULL REFERENCES store (store_id),
    active      boolean NOT NULL DEFAULT true,
    username    varchar(16) NOT NULL,
    password    varchar(40),
    last_update timestamp NOT NULL DEFAULT now(),
    picture     bytea
);

-- Customers of a store. Note the historical quirk carried over from Sakila: activebool is the
-- real boolean active flag, while the separate integer "active" column is a legacy marker.
-- last_update here is nullable (unlike most tables), matching the canonical schema.
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id    integer NOT NULL REFERENCES store (store_id),
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    email       varchar(50),
    address_id  integer NOT NULL REFERENCES address (address_id),
    activebool  boolean NOT NULL DEFAULT true,
    create_date date NOT NULL DEFAULT now(),
    last_update timestamp DEFAULT now(),
    active      integer
);

CREATE INDEX idx_fk_address_id ON customer (address_id);
CREATE INDEX idx_fk_store_id ON customer (store_id);
CREATE INDEX idx_last_name ON customer (last_name);

-- Physical inventory: each row is a copy of a film held at a particular store. The composite
-- index on (store_id, film_id) supports the common "what copies of this film does this store
-- have" lookup.
CREATE TABLE inventory
(
    inventory_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    film_id      integer NOT NULL REFERENCES film (film_id),
    store_id     integer NOT NULL REFERENCES store (store_id),
    last_update  timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_store_id_film_id ON inventory (store_id, film_id);

-- A rental transaction: a customer takes out an inventory item, handled by a staff member,
-- with an optional return_date that stays NULL until the item comes back. The unique index
-- prevents the same inventory item being rented to the same customer at the same instant.
CREATE TABLE rental
(
    rental_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    rental_date  timestamp NOT NULL,
    inventory_id integer NOT NULL REFERENCES inventory (inventory_id),
    customer_id  integer NOT NULL REFERENCES customer (customer_id),
    return_date  timestamp,
    staff_id     integer NOT NULL REFERENCES staff (staff_id),
    last_update  timestamp NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX idx_unq_rental_rental_date_inventory_id_customer_id
    ON rental (rental_date, inventory_id, customer_id);

CREATE INDEX idx_fk_inventory_id ON rental (inventory_id);
CREATE INDEX idx_fk_customer_id ON rental (customer_id);
CREATE INDEX idx_fk_staff_id ON rental (staff_id);

-- Payments taken against rentals. Each payment ties a customer, the staff member who took it,
-- and the rental being paid for; amount is a fixed-precision numeric.
CREATE TABLE payment
(
    payment_id   integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id  integer NOT NULL REFERENCES customer (customer_id),
    staff_id     integer NOT NULL REFERENCES staff (staff_id),
    rental_id    integer NOT NULL REFERENCES rental (rental_id),
    amount       numeric(5,2) NOT NULL,
    payment_date timestamp NOT NULL
);

CREATE INDEX idx_fk_customer_id ON payment (customer_id);
CREATE INDEX idx_fk_staff_id ON payment (staff_id);

-- A denormalised view of each actor together with the categories they appear in and, per
-- category, the comma-separated list of films. Exercises a correlated subquery feeding
-- group_concat inside an outer group_concat, and the -> / grouping done in a subselect. One of
-- the more advanced Sakila views.
CREATE VIEW actor_info AS
SELECT a.actor_id,
       a.first_name,
       a.last_name,
       group_concat(DISTINCT
           c.name || ': ' ||
           (SELECT group_concat(f.title)
            FROM film f
                JOIN film_category fc ON f.film_id = fc.film_id
                JOIN film_actor fa ON f.film_id = fa.film_id
            WHERE fc.category_id = c.category_id
              AND fa.actor_id = a.actor_id)
       ) AS film_info
FROM actor a
    LEFT JOIN film_actor fa ON a.actor_id = fa.actor_id
    LEFT JOIN film_category fc ON fa.film_id = fc.film_id
    LEFT JOIN category c ON fc.category_id = c.category_id
GROUP BY a.actor_id, a.first_name, a.last_name;

-- Flattens a customer together with their full address, city and country into a single row,
-- and renders the legacy integer "active" column as a human-readable notes string. Deployed
-- after the tables it joins.
CREATE VIEW customer_list AS
SELECT cu.customer_id AS id,
       cu.first_name || ' ' || cu.last_name AS name,
       a.address,
       a.postal_code AS "zip code",
       a.phone,
       city.city,
       country.country,
       CASE WHEN cu.activebool THEN 'active' ELSE '' END AS notes,
       cu.store_id AS sid
FROM customer cu
    JOIN address a ON cu.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;

-- A catalogue listing of films with their category and a comma-separated cast, built with the
-- user-defined group_concat aggregate (see Programmability/GroupConcat.sql). The aggregate is
-- deployed before this view. Filters to films rated below R for a "general" listing.
CREATE VIEW film_list AS
SELECT film.film_id AS fid,
       film.title,
       film.description,
       category.name AS category,
       film.rental_rate AS price,
       film.length,
       film.rating,
       group_concat(actor.first_name || ' ' || actor.last_name) AS actors
FROM film
    LEFT JOIN film_category ON film.film_id = film_category.film_id
    LEFT JOIN category ON film_category.category_id = category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, film.title, film.description, category.name,
         film.rental_rate, film.length, film.rating;

-- Same shape as film_list, but the cast names are title-cased for a "nicer" (and, as the name
-- warns, slower) presentation. Also built on the group_concat aggregate.
CREATE VIEW nicer_but_slower_film_list AS
SELECT film.film_id AS fid,
       film.title,
       film.description,
       category.name AS category,
       film.rental_rate AS price,
       film.length,
       film.rating,
       group_concat(
           upper(substring(actor.first_name, 1, 1)) || lower(substring(actor.first_name, 2)) || ' ' ||
           upper(substring(actor.last_name, 1, 1)) || lower(substring(actor.last_name, 2))
       ) AS actors
FROM film
    LEFT JOIN film_category ON film.film_id = film_category.film_id
    LEFT JOIN category ON film_category.category_id = category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, film.title, film.description, category.name,
         film.rental_rate, film.length, film.rating;

-- Total sales rolled up by film category, ordered from best-selling down. Walks the full
-- payment -> rental -> inventory -> film -> film_category -> category chain and sums the
-- payment amounts per category.
CREATE VIEW sales_by_film_category AS
SELECT c.name AS category,
       SUM(p.amount) AS total_sales
FROM payment p
    JOIN rental r ON p.rental_id = r.rental_id
    JOIN inventory i ON r.inventory_id = i.inventory_id
    JOIN film f ON i.film_id = f.film_id
    JOIN film_category fc ON f.film_id = fc.film_id
    JOIN category c ON fc.category_id = c.category_id
GROUP BY c.name
ORDER BY total_sales DESC;

-- Total sales per store, labelled with the store's city/country and its manager's name.
-- Demonstrates joining the sales chain against the store, its address hierarchy, and the
-- managing staff member.
CREATE VIEW sales_by_store AS
SELECT city.city || ',' || country.country AS store,
       m.first_name || ' ' || m.last_name AS manager,
       SUM(p.amount) AS total_sales
FROM payment p
    JOIN rental r ON p.rental_id = r.rental_id
    JOIN inventory i ON r.inventory_id = i.inventory_id
    JOIN store s ON i.store_id = s.store_id
    JOIN address a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id
    JOIN staff m ON s.manager_staff_id = m.staff_id
GROUP BY city.city, country.country, m.first_name, m.last_name
ORDER BY total_sales DESC;

-- The staff-facing counterpart to customer_list: each staff member with their full address,
-- city and country flattened into one row.
CREATE VIEW staff_list AS
SELECT s.staff_id AS id,
       s.first_name || ' ' || s.last_name AS name,
       a.address,
       a.postal_code AS "zip code",
       a.phone,
       city.city,
       country.country,
       s.store_id AS sid
FROM staff s
    JOIN address a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;

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

-- Computes a customer's outstanding balance as of a given date. The balance is the sum of
-- rental fees and any late/replacement charges, minus payments already made. A representative
-- example of a non-trivial plpgsql function that queries several tables and accumulates a
-- numeric result.
CREATE FUNCTION get_customer_balance(p_customer_id integer, p_effective_date timestamp)
RETURNS numeric
LANGUAGE plpgsql
AS $$
DECLARE
    v_rentfees numeric(5,2);  -- fees paid for rentals up to the effective date
    v_overfees integer;       -- late fees for outstanding overdue rentals
    v_payments numeric(5,2);  -- total payments made
BEGIN
    SELECT COALESCE(SUM(film.rental_rate), 0) INTO v_rentfees
    FROM film, inventory, rental
    WHERE film.film_id = inventory.film_id
      AND inventory.inventory_id = rental.inventory_id
      AND rental.rental_date <= p_effective_date
      AND rental.customer_id = p_customer_id;

    SELECT COALESCE(SUM(
        CASE WHEN (rental.return_date - rental.rental_date) > (film.rental_duration * '1 day'::interval)
            THEN EXTRACT(EPOCH FROM ((rental.return_date - rental.rental_date) - (film.rental_duration * '1 day'::interval))) / 86400
            ELSE 0
        END
    ), 0) INTO v_overfees
    FROM rental, inventory, film
    WHERE film.film_id = inventory.film_id
      AND inventory.inventory_id = rental.inventory_id
      AND rental.rental_date <= p_effective_date
      AND rental.customer_id = p_customer_id;

    SELECT COALESCE(SUM(payment.amount), 0) INTO v_payments
    FROM payment
    WHERE payment.payment_date <= p_effective_date
      AND payment.customer_id = p_customer_id;

    RETURN v_rentfees + v_overfees - v_payments;
END
$$;

-- The _group_concat state-transition function and the group_concat user-defined AGGREGATE
-- built on top of it. Together they reproduce MySQL's GROUP_CONCAT in PostgreSQL: given two
-- text values, _group_concat concatenates them with a ", " separator (skipping NULLs), and
-- the aggregate folds that over a group. Several of the Sakila views (film_list, actor_info,
-- nicer_but_slower_film_list) depend on this aggregate, so it is deployed before them.
CREATE FUNCTION _group_concat(text, text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
SELECT CASE
    WHEN $2 IS NULL THEN $1
    WHEN $1 IS NULL THEN $2
    ELSE $1 || ', ' || $2
END
$$;

CREATE AGGREGATE group_concat(text) (
    SFUNC = _group_concat,
    STYPE = text
);

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

-- Returns true if a given inventory item is available to rent. An item is in stock if it has
-- never been rented, or if all of its rentals have been returned. plpgsql with a boolean
-- result.
CREATE FUNCTION inventory_in_stock(p_inventory_id integer)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    v_rentals integer;
    v_out     integer;
BEGIN
    -- Has the item ever been rented?
    SELECT count(*) INTO v_rentals
    FROM rental
    WHERE inventory_id = p_inventory_id;

    IF v_rentals = 0 THEN
        RETURN true;
    END IF;

    -- Of those rentals, how many are still out (not yet returned)?
    SELECT count(rental_id) INTO v_out
    FROM inventory
        LEFT JOIN rental USING (inventory_id)
    WHERE inventory.inventory_id = p_inventory_id
      AND rental.return_date IS NULL;

    IF v_out > 0 THEN
        RETURN false;
    ELSE
        RETURN true;
    END IF;
END
$$;

-- Returns the last day of the month containing the given timestamp. A small, IMMUTABLE SQL
-- function used by the rentals reporting helpers. It works by truncating to the first of the
-- month, advancing one month, and subtracting a day.
CREATE FUNCTION last_day(timestamp)
RETURNS date
LANGUAGE sql
IMMUTABLE
STRICT
AS $$
SELECT (date_trunc('month', $1) + INTERVAL '1 month' - INTERVAL '1 day')::date
$$;

-- The trigger function behind every "last_update" column. It runs BEFORE UPDATE (see the
-- last-updated triggers under Triggers/) and stamps NEW.last_update with the current time, so
-- application code never has to set it. As a trigger function it takes no arguments and
-- returns the special "trigger" pseudo-type.
CREATE FUNCTION last_updated()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.last_update = now();
    RETURN NEW;
END
$$;

-- A reporting function that returns the set of customers who qualify for a rewards program in
-- the current month: those with at least a minimum number of monthly purchases and a minimum
-- dollar amount spent. It RETURNS SETOF customer — i.e. rows shaped like the customer table —
-- and builds its result into a TEMPORARY table before selecting it back out, illustrating
-- dynamic set-returning plpgsql. Marked SECURITY DEFINER in the canonical schema so it can be
-- granted to reporting roles.
CREATE FUNCTION rewards_report(
    min_monthly_purchases integer,
    min_dollar_amount_purchased numeric
)
RETURNS SETOF customer
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    last_month_start date;
    last_month_end   date;
    rr               RECORD;
    tmpSQL           text;
BEGIN
    -- Validate the arguments.
    IF min_monthly_purchases = 0 THEN
        RAISE EXCEPTION 'Minimum monthly purchases parameter must be > 0';
    END IF;
    IF min_dollar_amount_purchased = 0.00 THEN
        RAISE EXCEPTION 'Minimum monthly dollar amount purchased parameter must be > $0.00';
    END IF;

    -- Determine the bounds of the previous calendar month.
    last_month_start := CURRENT_DATE - '3 month'::interval;
    last_month_start := to_date(
        (extract(YEAR FROM last_month_start) || '-' || extract(MONTH FROM last_month_start) || '-01'),
        'YYYY-MM-DD');
    last_month_end := last_day(last_month_start);

    -- Collect the qualifying customer ids into a temporary table.
    CREATE TEMPORARY TABLE tmpCustomer (customer_id integer NOT NULL PRIMARY KEY);

    INSERT INTO tmpCustomer (customer_id)
    SELECT p.customer_id
    FROM payment AS p
    WHERE date(p.payment_date) BETWEEN last_month_start AND last_month_end
    GROUP BY customer_id
    HAVING SUM(p.amount) > min_dollar_amount_purchased
       AND COUNT(customer_id) > min_monthly_purchases;

    -- Return the full customer rows for the qualifying ids.
    tmpSQL := 'SELECT * FROM customer WHERE customer_id IN (SELECT customer_id FROM tmpCustomer)';
    FOR rr IN EXECUTE tmpSQL LOOP
        RETURN NEXT rr;
    END LOOP;

    DROP TABLE tmpCustomer;

    RETURN;
END
$$;

-- Stamps actor.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql). One such trigger exists per table that carries a
-- last_update column.
CREATE TRIGGER last_updated
    BEFORE UPDATE ON actor
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps address.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON address
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps category.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON category
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps city.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON city
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps country.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON country
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps customer.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON customer
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps film_actor.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON film_actor
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps film_category.last_update on every UPDATE via the shared last_updated() trigger
-- function (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON film_category
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Keeps film.fulltext (a tsvector) in sync with the film's title and description. Uses the
-- built-in tsvector_update_trigger function, whose arguments name the target tsvector column,
-- the text-search configuration ('pg_catalog.english'), and the source columns to index.
-- Fires BEFORE INSERT OR UPDATE so the search vector is always current before the row is
-- written. Paired with the GiST index film_fulltext_idx (see Tables/Film.sql).
CREATE TRIGGER film_fulltext_trigger
    BEFORE INSERT OR UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title', 'description');

-- Stamps film.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql). Independent of the film_fulltext_trigger, which
-- maintains the search vector on the same table.
CREATE TRIGGER last_updated
    BEFORE UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps inventory.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON inventory
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps language.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON language
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps rental.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON rental
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps staff.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON staff
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

-- Stamps store.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON store
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();

