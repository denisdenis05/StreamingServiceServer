FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PYTHONPATH=/opt/audiomuse AUDIOMUSE_BIND=0.0.0.0 AUDIOMUSE_PORT=8786
RUN apt-get update && apt-get install -y --no-install-recommends git ffmpeg libsndfile1 && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/bridge
COPY requirements.txt prepare_upstream.py ./
RUN pip install --no-cache-dir -r requirements.txt
RUN git init /opt/audiomuse && git -C /opt/audiomuse remote add origin https://github.com/NeptuneHub/AudioMuse-AI.git && git -C /opt/audiomuse fetch --depth=1 origin ac65c469d288a45092db77cc76c1e347a9e14f48 && git -C /opt/audiomuse checkout --detach FETCH_HEAD
RUN python prepare_upstream.py /opt/audiomuse
COPY bridge.py ./
RUN useradd --system --uid 10001 --create-home analyzer
USER analyzer
EXPOSE 8786
CMD ["python", "bridge.py"]
