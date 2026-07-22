-- The Sakila `customer_list` view. Flattens a customer together with its full address and
-- location (address -> city -> country) and derives a human-readable `notes` column from the
-- `active` flag using the MySQL `IF(...)` function and `CONCAT_WS` to join name parts.
CREATE VIEW customer_list AS
SELECT cu.customer_id                                    AS ID,
       CONCAT(cu.first_name, ' ', cu.last_name)          AS name,
       a.address                                         AS address,
       a.postal_code                                     AS `zip code`,
       a.phone                                           AS phone,
       city.city                                         AS city,
       country.country                                   AS country,
       IF(cu.active, 'active', '')                       AS notes,
       cu.store_id                                       AS SID
FROM customer AS cu
    JOIN address AS a ON cu.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;
