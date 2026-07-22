-- The Sakila `country` lookup table, the top of the country -> city -> address location
-- hierarchy. No foreign keys of its own; referenced by `city`.
CREATE TABLE country
(
    country_id  int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    country     varchar(50) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
