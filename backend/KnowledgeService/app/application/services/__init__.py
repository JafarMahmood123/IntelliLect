# Use cases / application services live here.
#
# Intentionally empty for the foundation task. Future work will add orchestrators
# such as:
#   - IngestDocumentService  (download -> extract -> OCR -> chunk -> embed -> store)
#   - RetrieveService        (embed_query -> vector search -> rank)
#
# These services will depend ONLY on the application ports (EmbeddingProvider,
# DocumentRepository, ChunkRepository) and domain entities — never on FastAPI,
# SQLAlchemy, or the google-genai SDK directly.
