-- The Sakila `sales_by_film_category` view. Aggregates total payment amount per film category,
-- following the payment -> rental -> inventory -> film -> film_category -> category chain.
-- Demonstrates `SUM`/`GROUP BY` over a long join path with an ordered result.
CREATE VIEW sales_by_film_category AS
SELECT c.name        AS category,
       SUM(p.amount) AS total_sales
FROM payment AS p
    INNER JOIN rental AS r ON p.rental_id = r.rental_id
    INNER JOIN inventory AS i ON r.inventory_id = i.inventory_id
    INNER JOIN film AS f ON i.film_id = f.film_id
    INNER JOIN film_category AS fc ON f.film_id = fc.film_id
    INNER JOIN category AS c ON fc.category_id = c.category_id
GROUP BY c.name
ORDER BY total_sales DESC;
