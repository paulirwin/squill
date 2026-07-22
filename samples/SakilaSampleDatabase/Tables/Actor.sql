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
