-- Flattens a customer together with their full address, city and country into a single row,
-- and renders the legacy integer "active" column as a human-readable notes string. Deployed
-- after the tables it joins.
CREATE VIEW customer_list AS
SELECT cu.customer_id AS id,
       cu.first_name || ' ' || cu.last_name AS name,
       a.address,
       a.postal_code AS "zip code",
       a.phone,
       city.city,
       country.country,
       CASE WHEN cu.activebool THEN 'active' ELSE '' END AS notes,
       cu.store_id AS sid
FROM customer cu
    JOIN address a ON cu.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;
