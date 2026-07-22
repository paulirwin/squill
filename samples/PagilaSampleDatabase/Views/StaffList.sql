-- The staff-facing counterpart to customer_list: each staff member with their full address,
-- city and country flattened into one row.
CREATE VIEW staff_list AS
SELECT s.staff_id AS id,
       s.first_name || ' ' || s.last_name AS name,
       a.address,
       a.postal_code AS "zip code",
       a.phone,
       city.city,
       country.country,
       s.store_id AS sid
FROM staff s
    JOIN address a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id;
