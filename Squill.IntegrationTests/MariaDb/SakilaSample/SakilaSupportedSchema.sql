-- =============================================================================
-- Test fixture: the Sakila (MariaDB / MySQL) sample schema.
--
-- Derived from the Sakila sample database for MySQL from the jOOQ Sakila repository
-- (https://github.com/jOOQ/sakila). The MySQL Sakila schema was originally created
-- by MySQL AB and is distributed under the BSD 3-Clause License below (its original
-- header); the jOOQ repository redistributing it is itself under the BSD 2-Clause
-- License. See samples/SakilaSampleDatabase/LICENSE.txt for both in full. The
-- applicable terms for this schema are the MySQL AB BSD 3-Clause terms. Generated
-- from the sample .sql files.
--
-- Sakila Sample Database Schema -- Version 0.8
--
-- Copyright (c) 2006, MySQL AB
-- All rights reserved.
--
-- Redistribution and use in source and binary forms, with or without modification,
-- are permitted provided that the following conditions are met:
--
--  * Redistributions of source code must retain the above copyright notice, this
--    list of conditions and the following disclaimer.
--  * Redistributions in binary form must reproduce the above copyright notice, this
--    list of conditions and the following disclaimer in the documentation and/or
--    other materials provided with the distribution.
--  * Neither the name of MySQL AB nor the names of its contributors may be used to
--    endorse or promote products derived from this software without specific prior
--    written permission.
--
-- THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
-- ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
-- WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
-- IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
-- INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
-- NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
-- PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
-- WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
-- ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
-- POSSIBILITY OF SUCH DAMAGE.
-- =============================================================================

-- The Sakila `country` lookup table, the top of the country -> city -> address location
-- hierarchy. No foreign keys of its own; referenced by `city`.
CREATE TABLE country
(
    country_id  int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    country     varchar(50) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- The Sakila `city` table, the middle of the location hierarchy. Holds a named foreign key
-- to `country` and a matching index on the FK column — Sakila names FK-backing indexes
-- `idx_fk_<column>` by convention.
CREATE TABLE city
(
    city_id     int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    city        varchar(50) NOT NULL,
    country_id  int unsigned NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_city_country FOREIGN KEY (country_id) REFERENCES country (country_id)
);

CREATE INDEX idx_fk_country_id ON city (country_id);

-- The Sakila `address` table, the bottom of the location hierarchy. Demonstrates nullable
-- columns (`address2`, `postal_code`) alongside NOT NULL columns, and a named foreign key to
-- `city` with its backing index.
--
-- The canonical Sakila schema carries a `location` GEOMETRY column here (a spatial type). It
-- is omitted from this sample because spatial types are an advanced, storage-engine-specific
-- feature outside the scope Squill models today.
CREATE TABLE address
(
    address_id  int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    address     varchar(50) NOT NULL,
    address2    varchar(50),
    district    varchar(20) NOT NULL,
    city_id     int unsigned NOT NULL,
    postal_code varchar(10),
    phone       varchar(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_address_city FOREIGN KEY (city_id) REFERENCES city (city_id)
);

CREATE INDEX idx_fk_city_id ON address (city_id);

-- The Sakila `language` lookup table. Uses a fixed-width `char(20)` for the name (rather than
-- `varchar`), matching the canonical schema. Referenced twice by `film` (spoken language and
-- original language).
CREATE TABLE language
(
    language_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name        char(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- The Sakila `category` lookup table for film genres. Joined to `film` through the
-- `film_category` junction table.
CREATE TABLE category
(
    category_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name        varchar(25) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- The Sakila `actor` table. A straightforward lookup table demonstrating the surrogate-key
-- pattern used throughout Sakila: `int unsigned NOT NULL AUTO_INCREMENT` primary key plus a
-- `last_update` audit column that MySQL/MariaDB keep current automatically.
--
-- `timestamp ... ON UPDATE CURRENT_TIMESTAMP` is a MySQL/MariaDB-specific feature: the column
-- defaults to the current time on INSERT and is refreshed to the current time on every UPDATE
-- of the row, with no trigger required.
CREATE TABLE actor
(
    actor_id    int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE INDEX idx_actor_last_name ON actor (last_name);

-- The Sakila `film` table — the richest table in the schema for MySQL/MariaDB-specific types.
-- It exercises several features found nowhere else in the sample:
--   * `year`    — the MySQL YEAR type (a 1-byte year value), used for `release_year`.
--   * `enum(...)` — a single-choice string type constrained to a fixed set of labels, here
--                 the MPAA `rating`.
--   * `set(...)`  — a multiple-choice string type: `special_features` may hold any subset of
--                 the listed labels in one column.
--   * `tinyint unsigned` / `smallint unsigned` — narrow unsigned integer types.
--   * `decimal(p,s)` columns with numeric DEFAULTs (`rental_rate`, `replacement_cost`).
--
-- Two foreign keys both point at `language`: `fk_film_language` for the spoken language and
-- `fk_film_language_original` for the original language (nullable). Sakila names the backing
-- indexes `idx_fk_language_id` and `idx_fk_original_language_id`.
CREATE TABLE film
(
    film_id              int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    title                varchar(255) NOT NULL,
    description          text,
    release_year         year,
    language_id          int unsigned NOT NULL,
    original_language_id int unsigned,
    rental_duration      tinyint unsigned NOT NULL DEFAULT 3,
    rental_rate          decimal(4, 2) NOT NULL DEFAULT 4.99,
    length               smallint unsigned,
    replacement_cost     decimal(5, 2) NOT NULL DEFAULT 19.99,
    rating               enum('G', 'PG', 'PG-13', 'R', 'NC-17') DEFAULT 'G',
    special_features     set('Trailers', 'Commentaries', 'Deleted Scenes', 'Behind the Scenes'),
    last_update          timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_film_language FOREIGN KEY (language_id) REFERENCES language (language_id),
    CONSTRAINT fk_film_language_original FOREIGN KEY (original_language_id) REFERENCES language (language_id)
);

CREATE INDEX idx_title ON film (title);
CREATE INDEX idx_fk_language_id ON film (language_id);
CREATE INDEX idx_fk_original_language_id ON film (original_language_id);

-- The Sakila `film_actor` junction table resolving the many-to-many relationship between
-- `film` and `actor`. Demonstrates a composite PRIMARY KEY over the two foreign-key columns
-- (there is no surrogate key here), plus named FKs to each parent and the Sakila-convention
-- backing index on the second FK column.
CREATE TABLE film_actor
(
    actor_id    int unsigned NOT NULL,
    film_id     int unsigned NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (actor_id, film_id),
    CONSTRAINT fk_film_actor_actor FOREIGN KEY (actor_id) REFERENCES actor (actor_id),
    CONSTRAINT fk_film_actor_film FOREIGN KEY (film_id) REFERENCES film (film_id)
);

CREATE INDEX idx_fk_film_id ON film_actor (film_id);

-- The Sakila `film_category` junction table resolving the many-to-many relationship between
-- `film` and `category`. Like `film_actor`, it uses a composite PRIMARY KEY over its two
-- foreign-key columns.
CREATE TABLE film_category
(
    film_id     int unsigned NOT NULL,
    category_id int unsigned NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (film_id, category_id),
    CONSTRAINT fk_film_category_film FOREIGN KEY (film_id) REFERENCES film (film_id),
    CONSTRAINT fk_film_category_category FOREIGN KEY (category_id) REFERENCES category (category_id)
);

-- The Sakila `film_text` table, kept in sync with `film` by the ins_film / upd_film / del_film
-- triggers (see Triggers/). It exists to exercise a MySQL FULLTEXT index: `FULLTEXT KEY`
-- builds an inverted index over the text columns so they can be searched with MATCH ... AGAINST
-- rather than only with LIKE.
--
-- Note this table uses a plain signed `int` primary key with no AUTO_INCREMENT — its `film_id`
-- is supplied by the triggers from `film`, not generated here — matching the canonical schema.
CREATE TABLE film_text
(
    film_id     int NOT NULL PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description text,
    FULLTEXT KEY idx_title_description (title, description)
);

-- The Sakila `store` table. Together with `staff` it forms a circular foreign-key dependency:
-- `store.manager_staff_id` references `staff`, while `staff.store_id` references `store`.
-- Sakila keeps both sides declarative with named FK constraints; a deployment tool must order
-- creation so that the constraints can be satisfied (typically by deferring or adding one FK
-- after both tables exist).
--
-- `idx_unique_manager` is a UNIQUE index enforcing that a staff member manages at most one
-- store.
CREATE TABLE store
(
    store_id         int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    manager_staff_id int unsigned NOT NULL,
    address_id       int unsigned NOT NULL,
    last_update      timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_store_staff FOREIGN KEY (manager_staff_id) REFERENCES staff (staff_id),
    CONSTRAINT fk_store_address FOREIGN KEY (address_id) REFERENCES address (address_id)
);

CREATE UNIQUE INDEX idx_unique_manager ON store (manager_staff_id);
CREATE INDEX idx_fk_address_id ON store (address_id);

-- The Sakila `staff` table. The other half of the circular store<->staff foreign-key pair
-- (see Store.sql). Demonstrates a `mediumblob` binary column (`picture`) and a `boolean`
-- column with a DEFAULT — in MySQL/MariaDB `boolean` is an alias for `tinyint(1)`.
CREATE TABLE staff
(
    staff_id    int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    address_id  int unsigned NOT NULL,
    picture     mediumblob,
    email       varchar(50),
    store_id    int unsigned NOT NULL,
    active      boolean NOT NULL DEFAULT true,
    username    varchar(16) NOT NULL,
    password    varchar(40),
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_staff_store FOREIGN KEY (store_id) REFERENCES store (store_id),
    CONSTRAINT fk_staff_address FOREIGN KEY (address_id) REFERENCES address (address_id)
);

CREATE INDEX idx_fk_store_id ON staff (store_id);
CREATE INDEX idx_fk_address_id ON staff (address_id);

-- The Sakila `customer` table. Demonstrates a `datetime` column with no auto-update
-- (`create_date`, set once at creation) alongside a `timestamp ... ON UPDATE CURRENT_TIMESTAMP`
-- audit column — a useful contrast between the two temporal types. Note that unlike the other
-- tables here, `last_update` is nullable (no NOT NULL), matching the canonical schema.
CREATE TABLE customer
(
    customer_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    store_id    int unsigned NOT NULL,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    email       varchar(50),
    address_id  int unsigned NOT NULL,
    active      boolean NOT NULL DEFAULT true,
    create_date datetime NOT NULL,
    last_update timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_customer_address FOREIGN KEY (address_id) REFERENCES address (address_id),
    CONSTRAINT fk_customer_store FOREIGN KEY (store_id) REFERENCES store (store_id)
);

CREATE INDEX idx_fk_store_id ON customer (store_id);
CREATE INDEX idx_fk_address_id ON customer (address_id);
CREATE INDEX idx_last_name ON customer (last_name);

-- The Sakila `inventory` table: one row per physical copy of a film held at a store.
-- Demonstrates a multi-column index (`idx_store_id_film_id`) in addition to the single-column
-- FK-backing index, and named foreign keys to both `store` and `film`.
CREATE TABLE inventory
(
    inventory_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    film_id      int unsigned NOT NULL,
    store_id     int unsigned NOT NULL,
    last_update  timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_inventory_store FOREIGN KEY (store_id) REFERENCES store (store_id),
    CONSTRAINT fk_inventory_film FOREIGN KEY (film_id) REFERENCES film (film_id)
);

CREATE INDEX idx_fk_film_id ON inventory (film_id);
CREATE INDEX idx_store_id_film_id ON inventory (store_id, film_id);

-- The Sakila `rental` table: one row per rental transaction. Demonstrates a composite UNIQUE
-- index across three columns (a business rule: the same copy cannot be rented to the same
-- customer at the same instant), a nullable `return_date`, and three named foreign keys with
-- their backing indexes.
--
-- Note the surrogate key is a plain signed `int` here (not `int unsigned`), matching the
-- canonical schema.
CREATE TABLE rental
(
    rental_id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
    rental_date  datetime NOT NULL,
    inventory_id int unsigned NOT NULL,
    customer_id  int unsigned NOT NULL,
    return_date  datetime,
    staff_id     int unsigned NOT NULL,
    last_update  timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT uq_rental_date_inventory_customer UNIQUE (rental_date, inventory_id, customer_id),
    CONSTRAINT fk_rental_staff FOREIGN KEY (staff_id) REFERENCES staff (staff_id),
    CONSTRAINT fk_rental_inventory FOREIGN KEY (inventory_id) REFERENCES inventory (inventory_id),
    CONSTRAINT fk_rental_customer FOREIGN KEY (customer_id) REFERENCES customer (customer_id)
);

CREATE INDEX idx_fk_inventory_id ON rental (inventory_id);
CREATE INDEX idx_fk_customer_id ON rental (customer_id);
CREATE INDEX idx_fk_staff_id ON rental (staff_id);

-- The Sakila `payment` table: one row per payment. Demonstrates a foreign key with a
-- referential action — `fk_payment_rental` is declared `ON DELETE SET NULL`, so deleting a
-- rental nulls out the payment's `rental_id` rather than blocking the delete (which is why
-- `rental_id` is nullable). `payment_id` uses `int unsigned` for its surrogate key.
CREATE TABLE payment
(
    payment_id   int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    customer_id  int unsigned NOT NULL,
    staff_id     int unsigned NOT NULL,
    rental_id    int,
    amount       decimal(5, 2) NOT NULL,
    payment_date datetime NOT NULL,
    last_update  timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_payment_rental FOREIGN KEY (rental_id) REFERENCES rental (rental_id) ON DELETE SET NULL,
    CONSTRAINT fk_payment_customer FOREIGN KEY (customer_id) REFERENCES customer (customer_id),
    CONSTRAINT fk_payment_staff FOREIGN KEY (staff_id) REFERENCES staff (staff_id)
);

CREATE INDEX idx_fk_staff_id ON payment (staff_id);
CREATE INDEX idx_fk_customer_id ON payment (customer_id);

-- The Sakila `actor_info` view — the most advanced view in the schema. For each actor it lists,
-- per category, a comma-separated roll-up of that actor's film titles. This is built with a
-- correlated subquery inside a `GROUP_CONCAT`, itself feeding an outer `GROUP_CONCAT` that pairs
-- each category name with its film list — a nested-aggregate pattern specific to MySQL/MariaDB.
--
-- The canonical Sakila schema declares this view with `SQL SECURITY INVOKER`; that clause is
-- preserved here.
CREATE
    SQL SECURITY INVOKER
VIEW actor_info AS
SELECT a.actor_id,
       a.first_name,
       a.last_name,
       GROUP_CONCAT(DISTINCT CONCAT(c.name, ': ',
           (SELECT GROUP_CONCAT(f.title ORDER BY f.title SEPARATOR ', ')
            FROM film f
                INNER JOIN film_category fc ON f.film_id = fc.film_id
                INNER JOIN film_actor fa ON f.film_id = fa.film_id
            WHERE fc.category_id = c.category_id
              AND fa.actor_id = a.actor_id))
           ORDER BY c.name SEPARATOR '; ') AS film_info
FROM actor a
    LEFT JOIN film_actor fa ON a.actor_id = fa.actor_id
    LEFT JOIN film_category fc ON fa.film_id = fc.film_id
    LEFT JOIN category c ON fc.category_id = c.category_id
GROUP BY a.actor_id, a.first_name, a.last_name;

-- The Sakila `customer_list` view. Flattens a customer together with its full address and
-- location (address -> city -> country) and derives a human-readable `notes` column from the
-- `active` flag using the MySQL `IF(...)` function and `CONCAT_WS` to join name parts.
CREATE VIEW customer_list AS
SELECT cu.customer_id                                    AS ID,
       CONCAT(cu.first_name, ' ', cu.last_name)          AS name,
       a.address                                         AS address,
       a.postal_code                                     AS `zip code`,
       a.phone                                           AS phone,
       city.city                                         AS city,
       country.country                                   AS country,
       IF(cu.active, 'active', '')                       AS notes,
       cu.store_id                                       AS SID
FROM customer AS cu
    JOIN address AS a ON cu.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;

-- The Sakila `film_list` view. Demonstrates `GROUP_CONCAT` (a MySQL/MariaDB aggregate that
-- concatenates the grouped actor names into a single string) together with a multi-table join
-- through the film_category / category and film_actor / actor junctions, grouped per film.
CREATE VIEW film_list AS
SELECT film.film_id                                        AS FID,
       film.title                                          AS title,
       film.description                                    AS description,
       category.name                                       AS category,
       film.rental_rate                                    AS price,
       film.length                                         AS length,
       film.rating                                         AS rating,
       GROUP_CONCAT(CONCAT(actor.first_name, ' ', actor.last_name) SEPARATOR ', ') AS actors
FROM film
    LEFT JOIN film_category ON film_category.film_id = film.film_id
    LEFT JOIN category ON category.category_id = film_category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, category.name;

-- The Sakila `nicer_but_slower_film_list` view. A variant of `film_list` that title-cases each
-- actor's name with nested MySQL string functions (`UPPER`, `SUBSTRING`, `CONCAT`,
-- `LOWER`) inside the `GROUP_CONCAT` — "nicer" output at the cost of the extra per-name work
-- ("slower"), as the canonical schema's name advertises.
CREATE VIEW nicer_but_slower_film_list AS
SELECT film.film_id                                        AS FID,
       film.title                                          AS title,
       film.description                                    AS description,
       category.name                                       AS category,
       film.rental_rate                                    AS price,
       film.length                                         AS length,
       film.rating                                         AS rating,
       GROUP_CONCAT(
           CONCAT(
               CONCAT(UCASE(SUBSTR(actor.first_name, 1, 1)),
                      LCASE(SUBSTR(actor.first_name, 2, LENGTH(actor.first_name))),
                      ' ',
                      CONCAT(UCASE(SUBSTR(actor.last_name, 1, 1)),
                             LCASE(SUBSTR(actor.last_name, 2, LENGTH(actor.last_name)))))
           ) SEPARATOR ', ')                               AS actors
FROM film
    LEFT JOIN film_category ON film_category.film_id = film.film_id
    LEFT JOIN category ON category.category_id = film_category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, category.name;

-- The Sakila `sales_by_film_category` view. Aggregates total payment amount per film category,
-- following the payment -> rental -> inventory -> film -> film_category -> category chain.
-- Demonstrates `SUM`/`GROUP BY` over a long join path with an ordered result.
CREATE VIEW sales_by_film_category AS
SELECT c.name        AS category,
       SUM(p.amount) AS total_sales
FROM payment AS p
    INNER JOIN rental AS r ON p.rental_id = r.rental_id
    INNER JOIN inventory AS i ON r.inventory_id = i.inventory_id
    INNER JOIN film AS f ON i.film_id = f.film_id
    INNER JOIN film_category AS fc ON f.film_id = fc.film_id
    INNER JOIN category AS c ON fc.category_id = c.category_id
GROUP BY c.name
ORDER BY total_sales DESC;

-- The Sakila `sales_by_store` view. Aggregates total payment amount per store, joining the
-- payment -> rental -> inventory -> store chain and decorating each store with its city/country
-- and manager name. Demonstrates `SUM` with `GROUP BY` and an `ORDER BY` on derived columns.
CREATE VIEW sales_by_store AS
SELECT CONCAT(c.city, ',', cy.country)             AS store,
       CONCAT(m.first_name, ' ', m.last_name)      AS manager,
       SUM(p.amount)                               AS total_sales
FROM payment AS p
    INNER JOIN rental AS r ON p.rental_id = r.rental_id
    INNER JOIN inventory AS i ON r.inventory_id = i.inventory_id
    INNER JOIN store AS s ON i.store_id = s.store_id
    INNER JOIN address AS a ON s.address_id = a.address_id
    INNER JOIN city AS c ON a.city_id = c.city_id
    INNER JOIN country AS cy ON c.country_id = cy.country_id
    INNER JOIN staff AS m ON s.manager_staff_id = m.staff_id
GROUP BY s.store_id
ORDER BY cy.country, c.city;

-- The Sakila `staff_list` view. Like `customer_list`, flattens a staff member together with
-- their address and location hierarchy into a single row.
CREATE VIEW staff_list AS
SELECT s.staff_id                                 AS ID,
       CONCAT(s.first_name, ' ', s.last_name)     AS name,
       a.address                                  AS address,
       a.postal_code                              AS `zip code`,
       a.phone                                    AS phone,
       city.city                                  AS city,
       country.country                            AS country,
       s.store_id                                 AS SID
FROM staff AS s
    JOIN address AS a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;

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

-- The Sakila `rewards_report` stored procedure. The most elaborate routine in the schema:
-- it validates its input parameters, creates a temporary table, populates it from an
-- aggregate query over `payment`, and returns the qualifying customers. Exercises IN
-- parameters, DECLARE of local variables, IF branching with `SELECT ... ` diagnostics,
-- and a `CREATE TEMPORARY TABLE ... SELECT`.
--
-- The body is stored verbatim by Squill and round-trips exactly as written here.
CREATE PROCEDURE rewards_report(
    IN min_monthly_purchases tinyint unsigned,
    IN min_dollar_amount_purchased decimal(10, 2),
    OUT count_rewardees int
)
    READS SQL DATA
    COMMENT 'Provides a customizable report on best customers'
proc: BEGIN

    DECLARE last_month_start DATE;
    DECLARE last_month_end DATE;

    /* Some sanity checks... */
    IF min_monthly_purchases = 0 THEN
        SELECT 'Minimum monthly purchases parameter must be > 0';
        LEAVE proc;
    END IF;
    IF min_dollar_amount_purchased = 0.00 THEN
        SELECT 'Minimum monthly dollar amount purchased parameter must be > $0.00';
        LEAVE proc;
    END IF;

    /* Determine start and end time periods */
    SET last_month_start = DATE_SUB(CURRENT_DATE(), INTERVAL 1 MONTH);
    SET last_month_start = STR_TO_DATE(CONCAT(YEAR(last_month_start), '-', MONTH(last_month_start), '-01'), '%Y-%m-%d');
    SET last_month_end = LAST_DAY(last_month_start);

    /*
        Create a temporary storage area for
        Customer IDs.
    */
    CREATE TEMPORARY TABLE tmpCustomer (customer_id INT NOT NULL PRIMARY KEY);

    /*
        Find all customers meeting the
        monthly purchase requirements
    */
    INSERT INTO tmpCustomer (customer_id)
    SELECT p.customer_id
    FROM payment AS p
    WHERE DATE(p.payment_date) BETWEEN last_month_start AND last_month_end
    GROUP BY p.customer_id
    HAVING SUM(p.amount) > min_dollar_amount_purchased
       AND COUNT(p.customer_id) > min_monthly_purchases;

    /* Populate OUT parameter with count of found customers */
    SELECT COUNT(*) FROM tmpCustomer INTO count_rewardees;

    /*
        Output ALL customer information of matching rewardees.
        Customize output as needed.
    */
    SELECT c.*
    FROM tmpCustomer AS t
        INNER JOIN customer AS c ON t.customer_id = c.customer_id;

    /* Clean up */
    DROP TABLE tmpCustomer;
END;

-- The Sakila `del_film` trigger. Fires AFTER DELETE ON `film` and removes the corresponding
-- row from `film_text`, completing the trio of triggers that keep the FULLTEXT-indexed copy in
-- sync with `film`. Exercises an AFTER DELETE row-level trigger and the `OLD` pseudo-row.
CREATE TRIGGER del_film
    AFTER DELETE
    ON film
    FOR EACH ROW
BEGIN
    DELETE FROM film_text WHERE film_id = OLD.film_id;
END;

-- The Sakila `ins_film` trigger. Fires AFTER INSERT ON `film` and copies the new film's
-- searchable text (title, description) into the `film_text` table, keeping the FULLTEXT-indexed
-- copy in sync. Exercises an AFTER INSERT row-level trigger and the `NEW` pseudo-row.
CREATE TRIGGER ins_film
    AFTER INSERT
    ON film
    FOR EACH ROW
BEGIN
    INSERT INTO film_text (film_id, title, description)
    VALUES (NEW.film_id, NEW.title, NEW.description);
END;

-- The Sakila `upd_film` trigger. Fires AFTER UPDATE ON `film` and, when any of the searchable
-- columns or the primary key change, propagates the change into `film_text`. Exercises an
-- AFTER UPDATE row-level trigger using both the `OLD` and `NEW` pseudo-rows inside an IF guard.
CREATE TRIGGER upd_film
    AFTER UPDATE
    ON film
    FOR EACH ROW
BEGIN
    IF (OLD.title != NEW.title)
        OR (OLD.description != NEW.description)
        OR (OLD.film_id != NEW.film_id)
    THEN
        UPDATE film_text
        SET title       = NEW.title,
            description = NEW.description,
            film_id     = NEW.film_id
        WHERE film_id = OLD.film_id;
    END IF;
END;

