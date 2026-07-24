-- User-defined types (issue #75): an ENUM type and a DOMAIN with a named CHECK, plus a
-- table with both an enum-typed column and a domain-typed column. A domain-typed column's
-- information_schema data_type reports the domain's base type, not the domain name, so the
-- DB-extraction builder resolves the domain name explicitly for the round-trip (issue #84).
CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');

CREATE DOMAIN year AS integer
    CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);

CREATE TABLE film (
    film_id integer PRIMARY KEY,
    title varchar(255),
    release_year year,
    rating mpaa_rating
);
