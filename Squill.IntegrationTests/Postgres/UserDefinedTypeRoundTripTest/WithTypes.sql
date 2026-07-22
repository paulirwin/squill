-- User-defined types (issue #75): an ENUM type and a DOMAIN with a named CHECK, plus a
-- table whose column is typed as the enum. The domain is exercised as a standalone object
-- (a domain-typed column's information_schema data_type reports the base type, not the
-- domain, so a domain-typed column does not yet round-trip — see the deploy test / issue).
CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');

CREATE DOMAIN year AS integer
    CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);

CREATE TABLE film (
    film_id integer PRIMARY KEY,
    title varchar(255),
    rating mpaa_rating
);
