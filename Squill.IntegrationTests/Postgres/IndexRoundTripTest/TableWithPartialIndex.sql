CREATE TABLE users
(
    id    integer PRIMARY KEY,
    email varchar(255)
);

CREATE INDEX idx_users_email ON users (email) WHERE email IS NOT NULL;
