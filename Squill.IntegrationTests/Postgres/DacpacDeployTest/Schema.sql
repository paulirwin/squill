CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email varchar(320) NOT NULL
);

CREATE TABLE orders
(
    order_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer (customer_id),
    description varchar(400) NOT NULL
);

CREATE INDEX ix_orders_customer_id ON orders (customer_id);
