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
