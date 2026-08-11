import http from 'k6/http'
import { check } from 'k6'
import { Rate, Trend } from 'k6/metrics'

const functionalErrors = new Rate('functional_errors')
const registerDuration = new Trend('register_duration', true)
const loginDuration = new Trend('login_duration', true)
const roomCreateDuration = new Trend('room_create_duration', true)
const historyDuration = new Trend('history_duration', true)

export const options = {
  vus: Number(__ENV.VUS || 1),
  iterations: Number(__ENV.ITERATIONS || 1),
  thresholds: {
    functional_errors: ['rate==0'],
    http_req_failed: ['rate==0'],
    register_duration: ['p(95)<1500'],
    login_duration: ['p(95)<1000'],
    room_create_duration: ['p(95)<1000'],
    history_duration: ['p(95)<1000'],
  },
}

const base = 'http://gateway:8080'

export default function () {
  const suffix = `${Date.now()}-${__VU}-${__ITER}`
  const email = `k6-${suffix}@example.test`
  const username = `k6_${suffix}`.slice(0, 40)
  const password = 'Password123'

  const register = http.post(`${base}/api/v1/auth/register`,
    JSON.stringify({ username, email, password }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'register' } })
  registerDuration.add(register.timings.duration)

  const registered = check(register, { 'register returns 201': r => r.status === 201 })
  functionalErrors.add(!registered)
  if (!registered) return

  const login = http.post(`${base}/api/v1/auth/login`,
    JSON.stringify({ email, password }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'login' } })
  loginDuration.add(login.timings.duration)
  const loggedIn = check(login, {
    'login returns 200': r => r.status === 200,
    'login returns access token': r => Boolean(r.json('accessToken')),
  })
  functionalErrors.add(!loggedIn)
  if (!loggedIn) return

  const token = login.json('accessToken')
  const authorized = {
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
  }

  const rooms = http.post(`${base}/api/v1/rooms`,
    JSON.stringify({ name: 'k6 room', description: 'smoke' }),
    { ...authorized, tags: { name: 'create-room' } })
  roomCreateDuration.add(rooms.timings.duration)

  const createdRoom = check(rooms, {
    'room returns 201': r => r.status === 201,
    'room returns id': r => Boolean(r.json('id')),
  })
  functionalErrors.add(!createdRoom)
  if (!createdRoom) return

  const roomId = rooms.json('id')
  const history = http.get(`${base}/api/v1/messages/room/${roomId}?take=20`, {
    headers: authorized.headers,
    tags: { name: 'history' },
  })
  historyDuration.add(history.timings.duration)
  const loadedHistory = check(history, {
    'history returns 200': r => r.status === 200,
    'new room history is empty': r => Array.isArray(r.json()) && r.json().length === 0,
  })
  functionalErrors.add(!loadedHistory)
}
