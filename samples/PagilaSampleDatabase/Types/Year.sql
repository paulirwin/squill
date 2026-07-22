-- A DOMAIN: a base type (integer) constrained by a named CHECK. film.release_year is typed
-- as this domain, so every value stored there is validated against the range without having
-- to repeat the constraint on each column. The domain is deployed before the tables that use
-- it.
CREATE DOMAIN year AS integer
    CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);
