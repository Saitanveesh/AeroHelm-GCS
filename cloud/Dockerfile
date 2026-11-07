FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /srv
COPY cloud/requirements.txt /srv/cloud/requirements.txt
RUN pip install --no-cache-dir -r /srv/cloud/requirements.txt \
 && groupadd --system aerohelm && useradd --system --gid aerohelm aerohelm
# Keep Mission Planner, native Windows binaries, dev tests, virtual models
# and downloaded artifacts out of the production cloud image.
COPY cloud/app /srv/cloud/app
COPY cloud/migrations /srv/cloud/migrations
COPY cloud/alembic.ini /srv/cloud/alembic.ini
USER aerohelm
EXPOSE 8080
# Migrate separately with: alembic -c cloud/alembic.ini upgrade head
# One API worker until cross-process authority leases are load/chaos tested.
CMD ["python","-m","uvicorn","cloud.app.main:create_app","--factory","--host","0.0.0.0","--port","8080","--workers","1","--limit-concurrency","150","--timeout-keep-alive","5"]
