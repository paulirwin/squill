-- Post-deployment script: runs after the schema changes are applied, on every deploy.
--
-- Unlike the declarative .sql files under Tables/ and Schemas/, this file is NOT parsed
-- into the schema model — it is stored verbatim in the DACPAC and executed as-is against
-- the target database. It is the place for seeding and data preparation.
--
-- Because it runs on EVERY deploy (including deploys that change no schema), write it to
-- be idempotent. Here that means ON CONFLICT DO NOTHING rather than a bare INSERT.
--
-- The SDK picks up PostDeploy.sql (and PreDeploy.sql) from the project root or a
-- Scripts/ subfolder automatically.

INSERT INTO author (author_id, name)
OVERRIDING SYSTEM VALUE
VALUES
    (1, 'Ursula K. Le Guin'),
    (2, 'Terry Pratchett')
ON CONFLICT (author_id) DO NOTHING;

INSERT INTO book (book_id, author_id, title)
OVERRIDING SYSTEM VALUE
VALUES
    (1, 1, 'A Wizard of Earthsea'),
    (2, 2, 'Good Omens')
ON CONFLICT (book_id) DO NOTHING;
