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
