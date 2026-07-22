-- The Sakila `category` lookup table for film genres. Joined to `film` through the
-- `film_category` junction table.
CREATE TABLE category
(
    category_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name        varchar(25) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
