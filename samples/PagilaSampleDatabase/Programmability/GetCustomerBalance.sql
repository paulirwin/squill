-- Computes a customer's outstanding balance as of a given date. The balance is the sum of
-- rental fees and any late/replacement charges, minus payments already made. A representative
-- example of a non-trivial plpgsql function that queries several tables and accumulates a
-- numeric result.
CREATE FUNCTION get_customer_balance(p_customer_id integer, p_effective_date timestamp)
RETURNS numeric
LANGUAGE plpgsql
AS $$
DECLARE
    v_rentfees numeric(5,2);  -- fees paid for rentals up to the effective date
    v_overfees integer;       -- late fees for outstanding overdue rentals
    v_payments numeric(5,2);  -- total payments made
BEGIN
    SELECT COALESCE(SUM(film.rental_rate), 0) INTO v_rentfees
    FROM film, inventory, rental
    WHERE film.film_id = inventory.film_id
      AND inventory.inventory_id = rental.inventory_id
      AND rental.rental_date <= p_effective_date
      AND rental.customer_id = p_customer_id;

    SELECT COALESCE(SUM(
        CASE WHEN (rental.return_date - rental.rental_date) > (film.rental_duration * '1 day'::interval)
            THEN EXTRACT(EPOCH FROM ((rental.return_date - rental.rental_date) - (film.rental_duration * '1 day'::interval))) / 86400
            ELSE 0
        END
    ), 0) INTO v_overfees
    FROM rental, inventory, film
    WHERE film.film_id = inventory.film_id
      AND inventory.inventory_id = rental.inventory_id
      AND rental.rental_date <= p_effective_date
      AND rental.customer_id = p_customer_id;

    SELECT COALESCE(SUM(payment.amount), 0) INTO v_payments
    FROM payment
    WHERE payment.payment_date <= p_effective_date
      AND payment.customer_id = p_customer_id;

    RETURN v_rentfees + v_overfees - v_payments;
END
$$;
