-- The Sakila `staff_list` view. Like `customer_list`, flattens a staff member together with
-- their address and location hierarchy into a single row.
CREATE VIEW staff_list AS
SELECT s.staff_id                                 AS ID,
       CONCAT(s.first_name, ' ', s.last_name)     AS name,
       a.address                                  AS address,
       a.postal_code                              AS `zip code`,
       a.phone                                    AS phone,
       city.city                                  AS city,
       country.country                            AS country,
       s.store_id                                 AS SID
FROM staff AS s
    JOIN address AS a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;
