-- Lookup table of countries, referenced by city. Top of the geographic hierarchy
-- (country -> city -> address), so it is deployed before the tables that reference it.
CREATE TABLE country
(
    country_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    country     varchar(50) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);
