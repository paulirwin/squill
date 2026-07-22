-- Total sales rolled up by film category, ordered from best-selling down. Walks the full
-- payment -> rental -> inventory -> film -> film_category -> category chain and sums the
-- payment amounts per category.
CREATE VIEW sales_by_film_category AS
SELECT c.name AS category,
       SUM(p.amount) AS total_sales
FROM payment p
    JOIN rental r ON p.rental_id = r.rental_id
    JOIN inventory i ON r.inventory_id = i.inventory_id
    JOIN film f ON i.film_id = f.film_id
    JOIN film_category fc ON f.film_id = fc.film_id
    JOIN category c ON fc.category_id = c.category_id
GROUP BY c.name
ORDER BY total_sales DESC;
