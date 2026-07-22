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
