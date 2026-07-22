-- Total sales per store, labelled with the store's city/country and its manager's name.
-- Demonstrates joining the sales chain against the store, its address hierarchy, and the
-- managing staff member.
CREATE VIEW sales_by_store AS
SELECT city.city || ',' || country.country AS store,
       m.first_name || ' ' || m.last_name AS manager,
       SUM(p.amount) AS total_sales
FROM payment p
    JOIN rental r ON p.rental_id = r.rental_id
    JOIN inventory i ON r.inventory_id = i.inventory_id
    JOIN store s ON i.store_id = s.store_id
    JOIN address a ON s.address_id = a.address_id
    JOIN city ON a.city_id = city.city_id
    JOIN country ON city.country_id = country.country_id
    JOIN staff m ON s.manager_staff_id = m.staff_id
GROUP BY city.city, country.country, m.first_name, m.last_name
ORDER BY total_sales DESC;
